# Core auth-contour locale files

These files are the **only** source of translations for the core sign-in window
(`DefaultAuthPageRenderer`) and the web confirmation page (`WebConfirmPage`). They are read
through the shared channel-contour localizer (`IConfirmationPromptLocalizer` /
`LocaleFileConfirmationPromptLocalizer`), which also serves the channel messages — one directory,
three consumers.

Natural Keys are English (base language, TASK-057): the key IS the English text, the value is the
translation. No translations live in code (TASK-062).

## Key parity is an invariant, enforced by a machine gate

Every `*.json` in this directory carries the **same key set**. This is not a convention — it is a
machine gate (`locale-parity`) that the maintainers run on every change to these files.

The gate catches:

- **key parity + placeholder parity** across every file of the directory (check1). The file set is
  the directory listing, so a language file added here is under the gate automatically — nothing
  to register anywhere;
- **keys used in code but missing from the files** (check2): the Natural Key constants of
  `AuthPageStrings`, `WebConfirmPage` and the message literals of `MessageTemplateNaturalKeys`. This
  is the direction parity alone cannot see — a key absent from *every* file keeps the files
  perfectly consistent with each other;
- **invalid JSON, duplicate keys within a file, Cyrillic in keys** (check1/check4);
- **a context label leaking into a value** (check8) — see «Context suffix» below.

The gate does **not** catch a **dynamically built key** (assembled at runtime rather than declared
as a constant) — it cannot be extracted from the code, so nothing proves it exists here.

Parity matters because a missing key degrades **silently**: resolution falls back to the key's
English base text (`IConfirmationPromptLocalizer.ResolveOrBaseText`), so the string renders in
English. Without the gate a half-filled file would ship a half-English page with no error anywhere.

A **blank** value (empty or whitespace-only) is treated exactly like a missing key — the same
fallback to the English base text. The gate compares key sets, not values, so it cannot catch a
blanked entry; degrading instead of passing it through is what keeps an emptied entry from shipping
a caption-less button or page control.

## Context suffix — one English phrase, two translations

Natural Keys make the key the English text, so one phrase gives one key and one translation. When
the same phrase must translate **differently** in different places, the key carries a semantic
context label after ` | ` (the gettext `msgctxt` idiom):

```json
"Sign-in confirmed ✅ | telegram": "登录已在 Telegram 中确认 ✅"
```

Rules that matter when editing these files:

- the label is **metadata, never text**. Whenever a translation is missing, both runtimes show the
  part before the **last** ` | `. The rule has three implementations: `NaturalKeyContext.BaseTextOf`
  here, and `JsonStringLocalizer` (server) and `i18n.js` (browser) in the site contour, which lives
  in the `veriqa/site` repository. That contour consumes the core as NuGet packages, so a change to
  the separator or the fallback has to land in all three — that is, in both repositories;
- **never write the key as its own value** for a suffixed key (`"X | tg": "X | tg"`) — check8 blocks
  it in the files of this repository. The core runtime treats such an entry as a miss and shows the
  base text (`ResolveOrBaseText`), which is what protects a locale file edited on an integrator's
  host; the site contour's localizers (repository `veriqa/site`) return it as is. Plain keys stay
  `key = value`;
- ` | ` is **reserved**: a plain UI string must not contain it, or the fallback would truncate real
  text;
- add a context **only on a real collision**. A channel's terminal outcome text is not one: outcome
  receipts are messages of the mechanism (SPEC-036 TPL-123), whose wording per channel and surface
  is chosen by the dimensions of the template key, not by a suffix on a Natural Key. The names of the
  built-in messaging apps are the one other use: the name key `<Name> | channel` (see below).

## Channel names and channel buttons

The button of a messaging app is a template with a slot for the channel name: `Sign in with {0}` on a
sign-in, `Confirm in {0}` on any other transaction. The slot takes:

- for a built-in messaging app, the translation of its name key `<Name> | channel` (`Telegram | channel`,
  `WeChat | channel` …). The same name appears on the button, on the tab and in the instruction, so
  translating it once covers all three — `zh.json` names WeChat `微信`;
- for a third-party channel, its declared label as is, untranslated.

The pages translate the name; the response of the confirmation creation API keeps it untranslated.

**A phrase for one channel outranks the template.** The page puts the English name into the template
(`Confirm in Viber`) and looks that phrase up as a key of its own. The order is:

1. an override of the phrase (`AuthPage.Texts`) — for a built-in messaging app only: the phrase of a
   third-party channel is not among the texts that can be overridden;
2. an override of the template;
3. a translation of the phrase in the locale file;
4. a translation of the template.

So a language whose grammar the template cannot hold translates the template once and adds phrases
only where needed — for Turkish, `"Confirm in {0}": "{0} üzerinden onayla"` and, for one channel,
`"Confirm in Viber": "Viber'de onayla"`. A phrase is optional in an integrator's own locale file. In the
files of this repository, a phrase added to one file goes into **every** file, like any other key — the
parity gate requires it. No code names a phrase as a key, so it also takes a line with a reason in
`build/english-first/i18n-lint/unused-keys.txt`.

## Adding a language

1. Drop `<code>.json` here with the **full key set** (copy any existing file and translate its
   values; the gate blocks an incomplete file).
2. **Restart the process.** The dictionaries are loaded once per process
   (`Lazy`/`ExecutionAndPublication`) — a file added or edited while the app is running is not
   picked up.
3. That is all. The language registry is data-driven — derived from the files actually loaded —
   so after the restart the language is live:
   - it is detected from `Accept-Language`;
   - `<html lang>` reflects it, because the pages resolve the *content* language by probing the
     files (a page is either fully in that language or fully on the English base — never mixed,
     WCAG 3.1.1);
   - it becomes a valid `Veriqa:Localization:DefaultLanguage`.

`Veriqa:Localization:DefaultLanguage` is validated against this same registry at startup: a value
with no file behind it fails the start (fail-fast, SPEC-012 §8.1) rather than silently serving
English. The registry is `<the locales loaded here> ∪ { base locale }`.

For **channel messages** the restart semantics are identical. The recipient's locale, however,
comes from the channel and is not checked against the window's registry — an unknown locale simply
degrades to the base language.

## Notes on individual locales

- **`en`** — the base language, and an ordinary translation file: resolution looks `en` up like any
  other locale, so **editing a value here changes the English text after a restart** — no code
  change, same as for `ru` or `zh`. Entries ship as `key = value`, which is why the file reads like
  a no-op today. A key missing from `en.json` (or the file itself missing) degrades to the key's own
  English base text, so the file stays optional at runtime — it is required for parity, and it is
  what makes the full key set reviewable in one place.
- **`pseudo`** — the layout test locale (`frontend/localization.md §6`): vowels doubled plus a
  ` <<<>>>` marker, to catch text that overflows its container. It is detectable via an explicit
  `Accept-Language: pseudo` (browsers never send it), but it is **rejected** as
  `Veriqa:Localization:DefaultLanguage` — it is a test artifact, not a production language.
  A pseudo-locale page therefore carries `<html lang="pseudo">`: the tag is syntactically valid
  but is not in the IANA registry, so assistive technology will not recognize it. That is expected
  and accepted for this locale — `lang` states the language the text is *actually* in, and the
  page is deliberately not in any real language. It is unreachable except by asking for it by
  name, so no production page is affected.
- **`zh`** — fully data-driven since TASK-062 (it used to live in a table in code).

## When adding a key

Add it to **every** file in this directory in the same commit, or the parity gate rejects the
change. Do not invent translations without a verified source — raise it with a maintainer instead.
