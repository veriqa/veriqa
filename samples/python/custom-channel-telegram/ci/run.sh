#!/usr/bin/env bash
# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# CI gate of the reference Telegram adapter — the one entry point, run from anywhere:
#
#   bash samples/python/custom-channel-telegram/ci/run.sh
#
# 1. Builds the images: the conformance suite, Veriqa, the adapter and the stand; starts the image of the
#    adapter without its transport secret, which must stop it with exit code 2 (startup.missing-env), and
#    with CODE_START set to a value it does not know, which must stop it the same way (startup.bad-code-start).
# 2. Starts the stand (ci/compose.stand.yaml) and runs the conformance suite in the automatic mode against
#    the adapter; every scenario must pass, except platform.cannot-continue, which the stand declares not
#    applicable. Then recreates that adapter with CODE_START=only and runs the suite again with the
#    registration declaring the start by a code (conformance.code-start): flow.code-start must pass as well.
# 3. Runs the own checks of the adapter (ci/checks.py) against the recorder of the platform stub, the start
#    by a code in the modes only, fallback and off among them; then reads the logs of the adapters of the
#    stand for the secrets of the run and the codes the own checks sent (logs.no-secrets), and recreates the
#    container of the standalone adapter between a call and its repeat (state.survives-recreate).
# 4. Starts compose.yaml with the CI overlay and waits until Veriqa reports the channel healthy.
#
# Prints one "PASS|FAIL <check>" line per step and, as the last two lines, the paths of the JSON reports of
# the suite: the run of the start by a code, then the first run. Secrets and tokens are generated for the
# run into a temporary directory outside the repository and removed with it; the stand is stopped whatever
# the outcome. Exit code 0 only when every step passed.

set -uo pipefail

SAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$SAMPLE_DIR/../../.." && pwd)"
readonly SAMPLE_DIR REPO_ROOT

readonly SUITE_IMAGE="veriqa-http-channel-conformance:latest"
readonly VERIQA_IMAGE="veriqa-authserver:latest"
readonly ADAPTER_IMAGE="veriqa-sample-telegram-adapter:latest"
readonly STAND_IMAGE="veriqa-sample-telegram-stand:latest"
readonly CHANNEL_TYPE="telegram-http"

# The instances of the adapter on the stand, whose logs must not carry a secret of the run, and the one the
# own checks of the tenants call, recreated by state.survives-recreate.
readonly ADAPTER_SERVICES=(adapter-suite adapter-forward adapter-standalone adapter-tenants adapter-code-only adapter-code-fallback)
readonly RECREATED_SERVICE="adapter-standalone"
readonly RECREATED_URL="http://adapter-standalone:8000/"

# Upper bounds of every wait, in seconds.
readonly BUILD_TIMEOUT=1800
readonly START_TIMEOUT=300
readonly WEBHOOK_WAIT=120
readonly SUITE_TIMEOUT=900
readonly CHECKS_TIMEOUT=300
readonly CHANNEL_WAIT=240
readonly COMMAND_TIMEOUT=120
readonly MISSING_ENV_EXIT_CODE=2

readonly STAND_PROJECT="veriqa-tg-stand-$$"
readonly SAMPLE_PROJECT="veriqa-tg-sample-$$"
readonly SUITE_CONTAINER="$STAND_PROJECT-suite"
readonly CODE_START_SUITE_CONTAINER="$STAND_PROJECT-suite-code-start"

# The second run of the suite: the adapter of the suite in the mode CODE_START=only, and the registration
# declaring the start by a code with a deep link that only opens the chat.
readonly SUITE_SERVICE="adapter-suite"
readonly SUITE_ADAPTER_URL="http://adapter-suite:8000/"
readonly CODE_START_MODE="only"
readonly CODE_START_KEY="Veriqa__Channels__${CHANNEL_TYPE}__Http__Capabilities__CodeStart__LinkCarriesTransaction"

# The report outlives the run; the working directory, which holds the secrets, does not.
REPORT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/veriqa-telegram-report.XXXXXX")"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/veriqa-telegram-stand.XXXXXX")"
readonly REPORT_DIR WORK_DIR
readonly REPORT_PATH="$REPORT_DIR/report.json"
readonly CODE_START_REPORT_PATH="$REPORT_DIR/report-code-start.json"
# The logs of the adapter of the suite before its recreation, read by logs.no-secrets.
readonly FIRST_SUITE_ADAPTER_LOG="$WORK_DIR/adapter-suite-first.txt"

failures=0

pass() { echo "PASS $1"; }

fail() {
    echo "FAIL $1${2:+: $2}"
    failures=$((failures + 1))
}

# Indents the output of a tool under the verdict lines of this script.
quote() { sed 's/^/    | /'; }

stand_compose() {
    local limit=$1
    shift
    timeout "$limit" docker compose -p "$STAND_PROJECT" -f "$SAMPLE_DIR/ci/compose.stand.yaml" \
        --env-file "$WORK_DIR/stand.env" "$@"
}

sample_compose() {
    local limit=$1
    shift
    timeout "$limit" docker compose -p "$SAMPLE_PROJECT" -f "$SAMPLE_DIR/compose.yaml" -f "$SAMPLE_DIR/ci/compose.ci.yaml" \
        --env-file "$WORK_DIR/sample.env" "$@"
}

cleanup() {
    {
        docker rm -f "$SUITE_CONTAINER" "$CODE_START_SUITE_CONTAINER"
        stand_compose "$COMMAND_TIMEOUT" --profile checks --profile suite down -v --remove-orphans
        sample_compose "$COMMAND_TIMEOUT" down -v --remove-orphans
    } >/dev/null 2>&1
    rm -rf "$WORK_DIR"
    echo "$CODE_START_REPORT_PATH"
    echo "$REPORT_PATH"
}

trap cleanup EXIT
trap 'exit 130' INT TERM

case "$WORK_DIR/" in
    "$REPO_ROOT"/*)
        fail setup "the temporary directory $WORK_DIR is inside the repository; point TMPDIR outside it"
        exit 1
        ;;
esac

# ── Docker ───────────────────────────────────────────────────────────────────
if ! command -v docker >/dev/null 2>&1 || ! timeout "$COMMAND_TIMEOUT" docker info >/dev/null 2>&1; then
    fail docker "Docker is not available: the stand needs a running Docker daemon and the docker CLI"
    exit 1
fi
if ! timeout "$COMMAND_TIMEOUT" docker compose version >/dev/null 2>&1; then
    fail docker "the Docker Compose plugin (docker compose) is not available"
    exit 1
fi
pass docker

# ── 1. Images ────────────────────────────────────────────────────────────────
build_image() {
    local name=$1
    shift
    if timeout "$BUILD_TIMEOUT" docker build "$@" >"$WORK_DIR/build.log" 2>&1; then
        return 0
    fi
    echo "    the image of $name did not build:"
    tail -n 40 "$WORK_DIR/build.log" | quote
    return 1
}

if build_image "the conformance suite" -f "$REPO_ROOT/src/core/Veriqa.Core.ChannelAdapter.Http.Conformance/Dockerfile" -t "$SUITE_IMAGE" "$REPO_ROOT" \
    && build_image "Veriqa" -f "$REPO_ROOT/build/docker/Dockerfile" -t "$VERIQA_IMAGE" "$REPO_ROOT" \
    && build_image "the adapter" -t "$ADAPTER_IMAGE" "$SAMPLE_DIR" \
    && build_image "the stand" -t "$STAND_IMAGE" "$SAMPLE_DIR/ci"; then
    pass images.build
else
    fail images.build "see the build output above"
    exit 1
fi

# ── Secrets of the run ───────────────────────────────────────────────────────
random_number() { od -An -N4 -tu4 /dev/urandom | tr -d ' \n'; }

# A transport secret: whsec_ and the base64 of 32 random bytes.
transport_secret() { printf 'whsec_%s' "$(head -c 32 /dev/urandom | base64 | tr -d '\n')"; }

# A bot token of the Telegram form: the numeric identifier of the bot, a colon, 36 URL-safe characters.
bot_token() { printf '%s:%s' "$1" "$(head -c 27 /dev/urandom | base64 | tr '+/' '-_' | tr -d '=\n')"; }

# 32 random hexadecimal digits: markers of the run and idempotency keys.
random_hex() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }

# A transaction id of Veriqa: 43 characters of Base62.
random_transaction() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 43; }

# $1 random decimal digits: the codes the own checks send to the bots of the start by a code.
random_digits() { LC_ALL=C tr -dc '0-9' </dev/urandom | head -c "$1"; }

user_id=$(($(random_number) % 900000000 + 100000000))
bot_base=$(($(random_number) % 900000000 + 100000000))
default_bot=$((bot_base + 1))
shop_a_bot=$((bot_base + 2))
shop_b_bot=$((bot_base + 3))
forward_bot=$((bot_base + 4))
standalone_bot=$((bot_base + 5))
sample_bot=$((bot_base + 6))
tenants_shop_a_bot=$((bot_base + 7))
tenants_shop_b_bot=$((bot_base + 8))
code_only_bot=$((bot_base + 10))
code_fallback_bot=$((bot_base + 11))

default_token="$(bot_token "$default_bot")"
shop_a_token="$(bot_token "$shop_a_bot")"
shop_b_token="$(bot_token "$shop_b_bot")"
forward_token="$(bot_token "$forward_bot")"
standalone_token="$(bot_token "$standalone_bot")"
tenants_shop_a_token="$(bot_token "$tenants_shop_a_bot")"
tenants_shop_b_token="$(bot_token "$tenants_shop_b_bot")"
code_only_token="$(bot_token "$code_only_bot")"
code_fallback_token="$(bot_token "$code_fallback_bot")"
# The texts the own checks send as codes, unique to the run; logs.no-secrets looks for them in the logs.
code_text="$(random_digits 12)"
code_text_separated="$(random_digits 4) $(random_digits 4)-$(random_digits 4)"
stand_secret="$(transport_secret)"
# A string of the run the own checks put into adapter_settings; logs.no-secrets looks for it in the logs.
settings_marker="stand-settings-$(random_hex)"

(
    umask 077
    cat >"$WORK_DIR/stand.env" <<EOF
STAND_USER_ID=$user_id
STAND_TRANSPORT_SECRET=$stand_secret
STAND_SETTINGS_MARKER=$settings_marker
STAND_DEFAULT_BOT_TOKEN=$default_token
STAND_SHOP_A_BOT_TOKEN=$shop_a_token
STAND_SHOP_A_BOT_ID=$shop_a_bot
STAND_SHOP_B_BOT_TOKEN=$shop_b_token
STAND_SHOP_B_BOT_ID=$shop_b_bot
STAND_FORWARD_TENANT=forward-shop
STAND_FORWARD_BOT_TOKEN=$forward_token
STAND_FORWARD_BOT_ID=$forward_bot
STAND_STANDALONE_BOT_TOKEN=$standalone_token
STAND_STANDALONE_BOT_ID=$standalone_bot
STAND_TENANTS_SHOP_A_BOT_TOKEN=$tenants_shop_a_token
STAND_TENANTS_SHOP_A_BOT_ID=$tenants_shop_a_bot
STAND_TENANTS_SHOP_B_BOT_TOKEN=$tenants_shop_b_token
STAND_TENANTS_SHOP_B_BOT_ID=$tenants_shop_b_bot
STAND_CODE_ONLY_TENANT=code-shop
STAND_CODE_ONLY_BOT_TOKEN=$code_only_token
STAND_CODE_ONLY_BOT_ID=$code_only_bot
STAND_CODE_FALLBACK_BOT_TOKEN=$code_fallback_token
STAND_CODE_FALLBACK_BOT_ID=$code_fallback_bot
STAND_CODE_TEXT=$code_text
STAND_CODE_TEXT_SEPARATED="$code_text_separated"
STAND_BOT_TOKENS=$default_token,$shop_a_token,$shop_b_token,$forward_token,$standalone_token,$tenants_shop_a_token,$tenants_shop_b_token,$code_only_token,$code_fallback_token
STAND_BOT_IDS=$default_bot,$shop_a_bot,$shop_b_bot,$forward_bot,$standalone_bot,$tenants_shop_a_bot,$tenants_shop_b_bot,$code_only_bot,$code_fallback_bot
EOF
    cat >"$WORK_DIR/sample.env" <<EOF
VERIQA_TRANSPORT_SECRET=$(transport_secret)
TELEGRAM_BOT_TOKEN=$(bot_token "$sample_bot")
TELEGRAM_INBOUND_MODE=webhook
VERIQA_INBOUND_VERIFICATION=shared_secret
TELEGRAM_PUBLIC_URL=http://adapter:8000
TELEGRAM_BOT_FORWARD_URL=
EOF
)

# ── startup.missing-env: the image of the adapter without its transport secret ──
# Every other required variable holds a value of this check alone, so that the output can be searched for it.
missing_env_url="http://core-$(random_hex).example:8080"
missing_env_public_url="http://adapter-$(random_hex).example:8000"
missing_env_token="$(bot_token "$((bot_base + 9))")"
missing_env_output="$(timeout "$COMMAND_TIMEOUT" docker run --rm --network none \
    -e VERIQA_URL="$missing_env_url" \
    -e VERIQA_CHANNEL_TYPE="$CHANNEL_TYPE" \
    -e TELEGRAM_INBOUND_MODE=webhook \
    -e TELEGRAM_PUBLIC_URL="$missing_env_public_url" \
    -e TELEGRAM_BOT_TOKEN="$missing_env_token" \
    "$ADAPTER_IMAGE" 2>&1)"
missing_env_code=$?
missing_env_leaked=()
for value in "$missing_env_url" "$CHANNEL_TYPE" webhook "$missing_env_public_url" "$missing_env_token" "${missing_env_token#*:}"; do
    if grep -qF -- "$value" <<<"$missing_env_output"; then
        missing_env_leaked+=("$value")
    fi
done
if [ "$missing_env_code" -ne "$MISSING_ENV_EXIT_CODE" ]; then
    echo "$missing_env_output" | tail -n 20 | quote
    fail startup.missing-env "the adapter without VERIQA_TRANSPORT_SECRET exited with code $missing_env_code, not $MISSING_ENV_EXIT_CODE"
elif ! grep -qF VERIQA_TRANSPORT_SECRET <<<"$missing_env_output"; then
    echo "$missing_env_output" | tail -n 20 | quote
    fail startup.missing-env "the output of the adapter does not name VERIQA_TRANSPORT_SECRET"
elif [ "${#missing_env_leaked[@]}" -gt 0 ]; then
    fail startup.missing-env "the output of the adapter carries the value of another variable: ${missing_env_leaked[*]}"
else
    pass startup.missing-env
fi

# ── startup.bad-code-start: the image of the adapter with CODE_START it does not know ──
# Every required variable is set, so that the value of CODE_START is what stops the adapter.
bad_code_start_secret="$(transport_secret)"
bad_code_start_token="$(bot_token "$((bot_base + 12))")"
bad_code_start_output="$(timeout "$COMMAND_TIMEOUT" docker run --rm --network none \
    -e VERIQA_URL="$missing_env_url" \
    -e VERIQA_CHANNEL_TYPE="$CHANNEL_TYPE" \
    -e VERIQA_TRANSPORT_SECRET="$bad_code_start_secret" \
    -e TELEGRAM_INBOUND_MODE=webhook \
    -e TELEGRAM_PUBLIC_URL="$missing_env_public_url" \
    -e TELEGRAM_BOT_TOKEN="$bad_code_start_token" \
    -e CODE_START=bogus \
    "$ADAPTER_IMAGE" 2>&1)"
bad_code_start_code=$?
bad_code_start_leaked=()
for value in "$bad_code_start_secret" "${bad_code_start_secret#whsec_}" "$bad_code_start_token" "${bad_code_start_token#*:}"; do
    if grep -qF -- "$value" <<<"$bad_code_start_output"; then
        bad_code_start_leaked+=("$value")
    fi
done
if [ "$bad_code_start_code" -ne "$MISSING_ENV_EXIT_CODE" ]; then
    echo "$bad_code_start_output" | tail -n 20 | quote
    fail startup.bad-code-start "the adapter with CODE_START=bogus exited with code $bad_code_start_code, not $MISSING_ENV_EXIT_CODE"
elif ! grep -qF CODE_START <<<"$bad_code_start_output"; then
    echo "$bad_code_start_output" | tail -n 20 | quote
    fail startup.bad-code-start "the output of the adapter does not name CODE_START"
elif [ "${#bad_code_start_leaked[@]}" -gt 0 ]; then
    fail startup.bad-code-start "the output of the adapter carries the transport secret or the token of the bot"
else
    pass startup.bad-code-start
fi

# ── 2. The stand and the conformance suite ───────────────────────────────────
show_stand_logs() {
    stand_compose "$COMMAND_TIMEOUT" logs --no-color --tail 60 "$@" 2>&1 | quote
}

# logs.no-secrets: the logs of every adapter of the stand, after the suite and the own checks, carry no
# secret of the run, no token of a bot, no adapter_settings, no identifier of the user in plain text and no
# code the own checks sent to a bot — as sent, nor its digits alone.
# Only the adapters are read: the stub answers the own checks with the secrets of the webhooks.
check_logs() {
    local log="$WORK_DIR/adapter-logs.txt" name value
    local -a leaked=()
    local -A values=(
        ["the transport secret"]="$stand_secret"
        ["the key of the transport secret"]="${stand_secret#whsec_}"
        ["the adapter_settings of the own checks"]="$settings_marker"
        ["the channel_user_id of the user of the stand"]="$user_id"
        ["the code the own checks sent"]="$code_text"
        ["the separated code the own checks sent"]="$code_text_separated"
        ["the digits of the separated code"]="$(tr -dc '0-9' <<<"$code_text_separated")"
    )
    local -a tokens=("$default_token" "$shop_a_token" "$shop_b_token" "$forward_token" "$standalone_token" "$tenants_shop_a_token" "$tenants_shop_b_token" "$code_only_token" "$code_fallback_token")
    for value in "${tokens[@]}"; do
        values["the token of bot ${value%%:*}"]="${value#*:}"
    done
    if ! stand_compose "$COMMAND_TIMEOUT" logs --no-color --no-log-prefix "${ADAPTER_SERVICES[@]}" >"$log" 2>/dev/null || [ ! -s "$log" ]; then
        fail logs.no-secrets "the logs of the adapters of the stand could not be read"
        return
    fi
    # The adapter of the suite was recreated for the run of the start by a code; its first logs were kept.
    if [ -s "$FIRST_SUITE_ADAPTER_LOG" ]; then
        cat "$FIRST_SUITE_ADAPTER_LOG" >>"$log"
    fi
    for name in "${!values[@]}"; do
        value="${values[$name]}"
        if [ -z "$value" ] || grep -qF -- "$value" "$log"; then
            leaked+=("$name")
        fi
    done
    if [ "${#leaked[@]}" -eq 0 ]; then
        pass logs.no-secrets
    else
        fail logs.no-secrets "the logs of the adapters carry $(IFS=';'; echo "${leaked[*]}")"
    fi
}

# state.survives-recreate: the idempotency key of a call and the tenant a deep link recorded for a transaction
# outlive a recreation of the container — a new container on the same data volume, not a restart of the old
# one, which would keep a store written beside the volume. The key and the transaction are made here and
# given to both phases; nothing clears the stub between them.
check_state_survives_recreate() {
    local key transaction before
    key="msg_$(random_hex)"
    transaction="$(random_transaction)"
    if ! before="$(stand_compose "$COMMAND_TIMEOUT" run --rm -T checks python checks.py survive-before "$key" "$transaction")"; then
        fail state.survives-recreate "the call before the recreation did not go through"
    elif ! stand_compose "$START_TIMEOUT" up -d --force-recreate --no-deps "$RECREATED_SERVICE" >"$WORK_DIR/recreate.log" 2>&1; then
        tail -n 20 "$WORK_DIR/recreate.log" | quote
        fail state.survives-recreate "the container of $RECREATED_SERVICE was not recreated"
    elif ! stand_compose "$((WEBHOOK_WAIT + COMMAND_TIMEOUT))" run --rm -T checks python checks.py wait-adapter "$RECREATED_URL" "$WEBHOOK_WAIT" \
        || ! stand_compose "$((WEBHOOK_WAIT + COMMAND_TIMEOUT))" run --rm -T checks python checks.py wait-webhooks "$WEBHOOK_WAIT"; then
        show_stand_logs "$RECREATED_SERVICE"
        fail state.survives-recreate "the recreated $RECREATED_SERVICE did not come up within the time allowed"
    elif ! stand_compose "$COMMAND_TIMEOUT" run --rm -T checks python checks.py survive-after "$key" "$transaction" "$before"; then
        fail state.survives-recreate "the repeat after the recreation did not find the state of the first call"
    else
        pass state.survives-recreate
    fi
}

stand_up=false
if stand_compose "$START_TIMEOUT" up -d >"$WORK_DIR/stand-up.log" 2>&1 \
    && stand_compose "$((WEBHOOK_WAIT + COMMAND_TIMEOUT))" run --rm -T checks python checks.py wait-webhooks "$WEBHOOK_WAIT"; then
    stand_up=true
    pass stand.up
else
    tail -n 40 "$WORK_DIR/stand-up.log" | quote
    show_stand_logs
    fail stand.up "the stub and the adapters did not come up within the time allowed"
fi

# run_suite <check> <container> <report> <argument of suite-report, or empty> [options of docker compose run]:
# one run of the suite against the adapter of the suite, its report copied out and read by checks.py.
run_suite() {
    local check=$1 container=$2 report=$3 report_argument=$4 code problem
    shift 4
    stand_compose "$SUITE_TIMEOUT" run -T --use-aliases --name "$container" "$@" suite 2>&1 | quote
    code=${PIPESTATUS[0]}
    docker cp "$container:/tmp/report.json" "$report" >/dev/null 2>&1

    if [ "$code" -eq 2 ]; then
        problem="the suite could not run (exit code 2): the stand is set up wrong, see its output above"
    elif [ ! -s "$report" ]; then
        problem="the suite left no report (exit code $code)"
    elif ! stand_compose "$COMMAND_TIMEOUT" run --rm -T checks python checks.py suite-report ${report_argument:+"$report_argument"} <"$report" \
        || [ "$code" -ne 0 ]; then
        problem="exit code $code; the scenarios that do not match are listed above"
    else
        problem=""
    fi
    if [ -z "$problem" ]; then
        pass "$check"
    else
        show_stand_logs "$SUITE_SERVICE" stub
        fail "$check" "$problem"
    fi
}

# conformance.code-start: the adapter of the suite recreated with CODE_START=only, then the second run of
# the suite with the registration declaring the start by a code. The variable goes into the env file, so
# that every later command of the stand sees the same configuration of the adapter.
run_code_start_suite() {
    if ! stand_compose "$COMMAND_TIMEOUT" logs --no-color --no-log-prefix "$SUITE_SERVICE" >"$FIRST_SUITE_ADAPTER_LOG" 2>/dev/null \
        || [ ! -s "$FIRST_SUITE_ADAPTER_LOG" ]; then
        fail conformance.code-start "not run: the logs of the first $SUITE_SERVICE could not be kept for logs.no-secrets"
        return
    fi
    echo "STAND_SUITE_CODE_START=$CODE_START_MODE" >>"$WORK_DIR/stand.env"
    if ! stand_compose "$START_TIMEOUT" up -d --force-recreate --no-deps "$SUITE_SERVICE" >"$WORK_DIR/recreate-suite.log" 2>&1; then
        tail -n 20 "$WORK_DIR/recreate-suite.log" | quote
        fail conformance.code-start "the container of $SUITE_SERVICE was not recreated with CODE_START=$CODE_START_MODE"
    elif ! stand_compose "$((WEBHOOK_WAIT + COMMAND_TIMEOUT))" run --rm -T checks python checks.py wait-adapter "$SUITE_ADAPTER_URL" "$WEBHOOK_WAIT"; then
        show_stand_logs "$SUITE_SERVICE"
        fail conformance.code-start "the recreated $SUITE_SERVICE did not come up within the time allowed"
    else
        run_suite conformance.code-start "$CODE_START_SUITE_CONTAINER" "$CODE_START_REPORT_PATH" code-start -e "$CODE_START_KEY=false"
    fi
}

if $stand_up; then
    run_suite conformance.suite "$SUITE_CONTAINER" "$REPORT_PATH" ""
    run_code_start_suite

    stand_compose "$CHECKS_TIMEOUT" run --rm -T checks
    checks_code=$?
    if [ "$checks_code" -eq 1 ]; then
        failures=$((failures + 1))
    elif [ "$checks_code" -ne 0 ]; then
        fail own.checks "the own checks did not complete (exit code $checks_code)"
    fi

    check_logs
    check_state_survives_recreate
else
    fail conformance.suite "not run: the stand is not up"
    fail conformance.code-start "not run: the stand is not up"
    fail own.checks "not run: the stand is not up"
    fail logs.no-secrets "not run: the stand is not up"
    fail state.survives-recreate "not run: the stand is not up"
fi

stand_compose "$COMMAND_TIMEOUT" --profile checks --profile suite down -v --remove-orphans >/dev/null 2>&1

# ── 3. compose.yaml next to Veriqa ───────────────────────────────────────────
if sample_compose "$START_TIMEOUT" up -d >"$WORK_DIR/sample-up.log" 2>&1 \
    && sample_compose "$((CHANNEL_WAIT + COMMAND_TIMEOUT))" exec -T stub \
        python checks.py wait-channel http://veriqa:8080/health/channels "$CHANNEL_TYPE" "$CHANNEL_WAIT"; then
    pass compose.up
else
    tail -n 40 "$WORK_DIR/sample-up.log" | quote
    sample_compose "$COMMAND_TIMEOUT" logs --no-color --tail 60 veriqa adapter 2>&1 | quote
    fail compose.up "Veriqa did not report $CHANNEL_TYPE healthy within $CHANNEL_WAIT s"
fi

if [ "$failures" -eq 0 ]; then
    exit 0
fi
exit 1
