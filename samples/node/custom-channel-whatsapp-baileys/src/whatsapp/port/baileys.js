// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The port of the platform in the ``baileys`` mode: one WhatsApp Web session per record, through Baileys.
//
// Baileys is an unofficial client of WhatsApp Web: the adapter is linked to the phone of each record as a
// companion device. A record without a session state is paired by a pairing code for its own number; the
// code is written to a file next to the session state, never to the log. The session state is kept in a
// directory of its own per record by ``useMultiFileAuthState`` of the library.
//
// Nothing the library or its dependencies write reaches the log: the library gets a logger that drops every
// line — its lines carry numbers and addresses — and the console is silenced, since the Signal library under
// Baileys prints its session records, keys included, to it.

import { createHash } from "node:crypto";
import { rm, writeFile } from "node:fs/promises";
import { join } from "node:path";

import makeWASocket, {
  DisconnectReason,
  decryptPollVote,
  generateMessageIDV2,
  isJidGroup,
  isLidUser,
  isPnUser,
  jidDecode,
  jidEncode,
  jidNormalizedUser,
  normalizeMessageContent,
  useMultiFileAuthState,
} from "baileys";

import { logger, sleep } from "../../common.js";
import { PlatformError } from "../../core/seam.js";
import { normalizePhoneToE164 } from "../phone.js";
import { ChatKind, MessageType, PLATFORM_TIMEOUT_MS, RecordState } from "../port.js";

const log = logger("whatsapp.baileys");

/** The server of the addresses of WhatsApp Web users by phone number. */
const USER_SERVER = "s.whatsapp.net";

/** The name of the file of the pairing code of a record: its label and this suffix, next to its session. */
export const PAIRING_CODE_SUFFIX = ".pairing-code";

// Pauses between reconnections of a session: doubled from the first up to the last, in milliseconds.
const FIRST_RECONNECT_PAUSE_MS = 1_000;
const MAX_RECONNECT_PAUSE_MS = 60_000;

// Access to the files of a pairing code: the owner alone.
const PAIRING_CODE_FILE_MODE = 0o600;

// A poll with one choice: one answer per vote.
const SELECTABLE_OPTIONS = 1;

/** A logger of the library that writes nothing. */
const SILENT_LOGGER = {
  level: "silent",
  child() {
    return SILENT_LOGGER;
  },
  trace() {},
  debug() {},
  info() {},
  warn() {},
  error() {},
};

/** Silences the console for the process: the Signal library under Baileys prints session records to it. */
function silenceConsole() {
  for (const method of ["log", "info", "warn", "error", "debug", "trace"]) {
    console[method] = () => {};
  }
}

export class BaileysPort {
  constructor(config, polls) {
    this.sessions = new Map(config.accounts.map((account) => [account.label, new Session(account, config.sessionsDir, polls)]));
    this.pending = new Set();
  }

  async start(onMessage) {
    silenceConsole();
    const handle = (message) => {
      const handling = onMessage(message)
        .catch((error) => log.error(`An inbound message of record ${message.account.label} failed: ${error?.name ?? typeof error}.`))
        .finally(() => this.pending.delete(handling));
      this.pending.add(handling);
    };
    for (const session of this.sessions.values()) {
      session.start(handle);
    }
    log.info(`Baileys mode: ${this.sessions.size} record(s) connecting to WhatsApp Web.`);
  }

  /** Ends every session, then waits for the inbound messages still being handled. */
  async stop() {
    await Promise.all([...this.sessions.values()].map((session) => session.stop()));
    await Promise.all([...this.pending]);
  }

  newMessageId(account) {
    return generateMessageIDV2(this.sessionOf(account).socket?.user?.id);
  }

  async sendText(account, recipient, text) {
    const sent = await this.sessionOf(account).send(recipient, { text }, {});
    return sent.key.id;
  }

  async sendPoll(account, recipient, poll) {
    const content = {
      poll: { name: poll.question, values: poll.options, selectableCount: SELECTABLE_OPTIONS, messageSecret: poll.secret },
    };
    await this.sessionOf(account).send(recipient, content, { messageId: poll.messageId });
  }

  async stateOf(account) {
    return this.sessionOf(account).state;
  }

  sessionOf(account) {
    return this.sessions.get(account.label);
  }
}

/**
 * The WhatsApp Web session of one record: connected at the start, reconnected after a drop with a pause
 * that grows, and left closed after a log-out or a pairing with another number — the operator acts then.
 */
class Session {
  constructor(account, sessionsDir, polls) {
    this.account = account;
    this.polls = polls;
    this.directory = join(sessionsDir, account.label);
    this.pairingCodeFile = join(sessionsDir, account.label + PAIRING_CODE_SUFFIX);
    this.state = RecordState.NOT_CONNECTED;
    this.socket = null;
    this.onMessage = null;
    this.stopped = false;
    this.reconnectPauseMs = FIRST_RECONNECT_PAUSE_MS;
    this.reconnectTimer = null;
    // The inbound messages still being read — a read may wait on the mapping of a LID: stop() waits for them.
    this.reading = new Set();
  }

  start(onMessage) {
    this.onMessage = onMessage;
    this.connect().catch((error) => this.connectFailed(error));
  }

  async stop() {
    this.stopped = true;
    clearTimeout(this.reconnectTimer);
    const socket = this.socket;
    this.socket = null;
    await socket?.end(undefined);
    await Promise.all([...this.reading]);
  }

  /** Opens a socket over the session state of the record; a record without one starts pairing. */
  async connect() {
    const { state, saveCreds } = await useMultiFileAuthState(this.directory);
    if (this.stopped) {
      return;
    }
    const socket = makeWASocket({
      auth: state,
      logger: SILENT_LOGGER,
      // The phone of the record keeps its notifications, and no full history is asked for: the adapter
      // reads only the messages that arrive for it.
      markOnlineOnConnect: false,
      syncFullHistory: false,
    });
    this.socket = socket;
    let pairingRequested = false;
    let socketClosed = false;

    socket.ev.on("creds.update", () => {
      saveCreds().catch((error) => log.error(`The session state of record ${this.account.label} was not saved (${error?.code ?? error?.name}).`));
    });
    socket.ev.on("connection.update", (update) => {
      if (update.connection === "close") {
        // The code a socket gave dies with it, whatever closed it: its file does not outlive it.
        socketClosed = true;
        if (pairingRequested) {
          this.removePairingCode();
        }
      }
      if (socket !== this.socket) {
        return;
      }
      if (update.qr !== undefined && !pairingRequested) {
        // A socket that is offered a QR code has no session: it is paired by a code for the own number.
        pairingRequested = true;
        this.state = RecordState.NOT_PAIRED;
        this.requestPairingCode(socket, () => socketClosed);
      }
      if (update.connection === "open") {
        this.opened(socket);
      } else if (update.connection === "close") {
        this.closed(update.lastDisconnect?.error);
      }
    });
    // Every upsert is read, "append" as well as "notify": the messages that came while the socket was
    // offline are upserted as "append". The own messages of the number come there too, and are dropped.
    socket.ev.on("messages.upsert", ({ messages }) => {
      if (socket !== this.socket) {
        return;
      }
      for (const message of messages) {
        const reading = this.read(socket, message)
          .then((read) => read !== null && this.onMessage(read))
          .catch((error) => log.error(`An inbound message of record ${this.account.label} was not read: ${error?.name ?? typeof error}.`))
          .finally(() => this.reading.delete(reading));
        this.reading.add(reading);
      }
    });
  }

  /** Asks for a pairing code and writes it to the file of the record, unless ``isClosed`` says the socket is gone. */
  async requestPairingCode(socket, isClosed) {
    try {
      const code = await socket.requestPairingCode(this.account.digits);
      await writeFile(this.pairingCodeFile, `${code}\n`, { mode: PAIRING_CODE_FILE_MODE });
      if (isClosed()) {
        // The socket closed while the code was being written: the code is dead already.
        this.removePairingCode();
        return;
      }
      log.info(
        `Record ${this.account.label} is not paired: enter the pairing code of the file ${this.pairingCodeFile} `
          + "on the phone of its number, under Linked devices.",
      );
    } catch (error) {
      log.warning(`No pairing code was made for record ${this.account.label}: ${error?.name ?? typeof error}.`);
    }
  }

  removePairingCode() {
    rm(this.pairingCodeFile, { force: true }).catch(() => {});
  }

  /** The socket is open: the session is in order when it is the session of the number of the record. */
  opened(socket) {
    const pairedDigits = jidDecode(socket.user?.id)?.user;
    if (pairedDigits !== this.account.digits) {
      this.state = RecordState.NOT_PAIRED;
      this.stopped = true;
      log.warning(
        `Record ${this.account.label} is paired with another number than its own, or its number is not in the form `
          + `WhatsApp addresses it; its session is closed. Correct the number of the record, or remove ${this.directory} `
          + "and restart the adapter to pair it again.",
      );
      void socket.end(undefined);
      return;
    }
    this.state = RecordState.OK;
    this.reconnectPauseMs = FIRST_RECONNECT_PAUSE_MS;
    this.removePairingCode();
    log.info(`Record ${this.account.label} is connected to WhatsApp Web.`);
  }

  /** The socket closed: a log-out ends the session, any other drop is reconnected after a pause. */
  closed(error) {
    if (this.stopped) {
      return;
    }
    this.socket = null;
    const status = error?.output?.statusCode;
    if (status === DisconnectReason.loggedOut) {
      this.state = RecordState.NOT_PAIRED;
      this.stopped = true;
      this.removePairingCode();
      log.warning(
        `Record ${this.account.label} was logged out of WhatsApp and is not paired. `
          + `Remove ${this.directory} and restart the adapter to pair it again.`,
      );
      return;
    }
    // A record that was pairing stays not paired until a socket of it opens; any other is not connected.
    if (this.state !== RecordState.NOT_PAIRED) {
      this.state = RecordState.NOT_CONNECTED;
    }
    this.scheduleReconnect(`closed with status ${status ?? "unknown"}`);
  }

  connectFailed(error) {
    this.state = RecordState.NOT_CONNECTED;
    // Only the code of the failure is given: its text may carry the path of the session.
    this.scheduleReconnect(`did not open (${error?.code ?? error?.name ?? typeof error})`);
  }

  scheduleReconnect(reason) {
    if (this.stopped) {
      return;
    }
    const pause = this.reconnectPauseMs;
    this.reconnectPauseMs = Math.min(pause * 2, MAX_RECONNECT_PAUSE_MS);
    log.warning(`The session of record ${this.account.label} ${reason}; reconnecting in ${pause / 1000} s.`);
    this.reconnectTimer = setTimeout(() => {
      this.connect().catch((failure) => this.connectFailed(failure));
    }, pause);
  }

  /** Sends ``content`` to the number ``recipient`` in E.164 within the timeout of an operation. */
  async send(recipient, content, options) {
    if (this.state !== RecordState.OK || this.socket === null) {
      throw new PlatformError(`record ${this.account.label} is ${this.state}`);
    }
    const jid = jidEncode(recipient.slice(1), USER_SERVER);
    let sent;
    try {
      sent = await Promise.race([
        this.socket.sendMessage(jid, content, options),
        sleep(PLATFORM_TIMEOUT_MS).then(() => {
          throw new PlatformError(`the sending did not end within ${PLATFORM_TIMEOUT_MS / 1000} s`);
        }),
      ]);
    } catch (error) {
      if (error instanceof PlatformError) {
        throw error;
      }
      // Only the status of the failure is given: the text of an error of the library may carry an address.
      throw new PlatformError(`the sending failed (${error?.output?.statusCode ?? error?.name ?? typeof error})`);
    }
    if (typeof sent?.key?.id !== "string") {
      throw new PlatformError("the sending gave no message");
    }
    return sent;
  }

  /** One message of WhatsApp Web as the classification reads it, or ``null`` when it has no identifier. */
  async read(socket, message) {
    const key = message.key ?? {};
    if (typeof key.id !== "string" || !key.id) {
      return null;
    }
    const chat = chatKindOf(key.remoteJid);
    const read = {
      account: this.account,
      messageId: key.id,
      chat,
      fromMe: key.fromMe === true,
      sender: null,
      pushName: typeof message.pushName === "string" ? message.pushName : null,
      type: MessageType.OTHER,
      text: null,
      pollMessageId: null,
      selectedOptions: null,
    };
    if (chat !== ChatKind.PERSONAL || read.fromMe) {
      return read;
    }

    read.sender = await senderOf(socket, key);
    const content = normalizeMessageContent(message.message);
    const text = content?.conversation ?? content?.extendedTextMessage?.text;
    if (typeof text === "string") {
      return { ...read, type: MessageType.TEXT, text };
    }
    const update = content?.pollUpdateMessage;
    if (update) {
      const pollMessageId = update.pollCreationMessageKey?.id ?? null;
      return {
        ...read,
        type: MessageType.POLL_VOTE,
        pollMessageId,
        selectedOptions: pollMessageId === null ? null : this.readVote(socket, key, update.vote, pollMessageId),
      };
    }
    return read;
  }

  /**
   * The options a vote selects, or ``null`` when the poll is not kept or the vote does not decrypt.
   *
   * The vote is encrypted with the secret of the poll over the addresses of the poll's creator and of the
   * voter as the voter's phone knows them: either may be a phone-number address or a LID, so each known
   * address of both is tried. The encryption is authenticated, so a wrong pair fails rather than misreads.
   */
  readVote(socket, voteKey, encrypted, pollMessageId) {
    const poll = this.polls.pollOf(this.account.label, pollMessageId);
    if (poll === null || !encrypted?.encPayload || !encrypted?.encIv) {
      return null;
    }
    const creators = distinctUsers([socket.user?.id, socket.user?.lid]);
    const voters = distinctUsers([voteKey.participantAlt, voteKey.remoteJidAlt, voteKey.participant, voteKey.remoteJid]);
    for (const pollCreatorJid of creators) {
      for (const voterJid of voters) {
        let vote;
        try {
          vote = decryptPollVote(encrypted, { pollCreatorJid, pollMsgId: pollMessageId, pollEncKey: poll.secret, voterJid });
        } catch {
          continue;
        }
        const options = [poll.confirmOption, poll.declineOption];
        return (vote.selectedOptions ?? [])
          .map((hash) => options.find((option) => sameHash(hash, option)))
          .filter((option) => option !== undefined);
      }
    }
    return null;
  }
}

/** The kind of the chat of an address of WhatsApp Web. */
function chatKindOf(remoteJid) {
  if (isJidGroup(remoteJid)) {
    return ChatKind.GROUP;
  }
  return isPnUser(remoteJid) || isLidUser(remoteJid) ? ChatKind.PERSONAL : ChatKind.OTHER;
}

/**
 * The number of the sender of a personal message in E.164: from its phone-number address, or from the
 * phone-number address WhatsApp gives beside a LID, or from the mapping of LIDs the library keeps. ``null``
 * when none of them gives a number.
 */
async function senderOf(socket, key) {
  for (const jid of [key.remoteJid, key.remoteJidAlt]) {
    if (isPnUser(jid)) {
      return normalizePhoneToE164(`+${jidDecode(jid)?.user ?? ""}`);
    }
  }
  if (isLidUser(key.remoteJid)) {
    try {
      const mapped = await socket.signalRepository.lidMapping.getPNForLID(key.remoteJid);
      if (isPnUser(mapped)) {
        return normalizePhoneToE164(`+${jidDecode(mapped)?.user ?? ""}`);
      }
    } catch {
      // No mapping: the sender stays without a number.
    }
  }
  return null;
}

/** The distinct user addresses of ``jids`` without a device part; absent ones are left out. */
function distinctUsers(jids) {
  return [...new Set(jids.filter((jid) => typeof jid === "string" && jid).map((jid) => jidNormalizedUser(jid)))].filter(Boolean);
}

/** Whether a selected option of a vote — the SHA-256 of the option's text — is the option ``option``. */
function sameHash(hash, option) {
  return hash instanceof Uint8Array && createHash("sha256").update(option, "utf8").digest().equals(hash);
}
