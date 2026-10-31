#!/usr/bin/env bash
# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# CI gate of the reference WhatsApp adapter through Twilio — the one entry point, run from anywhere:
#
#   bash samples/node/custom-channel-whatsapp-twilio/ci/run.sh
#
# 1. Builds the images: the conformance suite, Veriqa, the adapter and the stand; starts the image of the
#    adapter without its transport secret, which must stop it with exit code 2 (startup.missing-env).
# 2. Starts the stand (ci/compose.stand.yaml) and runs the conformance suite in the automatic mode against
#    the adapter; every scenario must pass, platform.cannot-continue (the closed customer service window of
#    WhatsApp) included — none may be not applicable.
# 3. Runs the own checks of the adapter (ci/checks.mjs) against the recorder of the platform stub; then reads
#    the logs of the adapters of the stand for the secrets of the run (logs.no-secrets), and recreates the
#    container of the adapter of the checks between a call and its repeat (state.survives-recreate).
# 4. Starts compose.yaml with the CI overlay and waits until Veriqa reports the channel healthy.
#
# Prints one "PASS|FAIL <check>" line per step and, as the last line, the path of the JSON report of the
# suite. Secrets, SIDs and numbers are generated for the run into a temporary directory outside the
# repository and removed with it; the stand is stopped whatever the outcome. Exit code 0 only when every
# step passed.

set -uo pipefail

SAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$SAMPLE_DIR/../../.." && pwd)"
readonly SAMPLE_DIR REPO_ROOT

readonly SUITE_IMAGE="veriqa-http-channel-conformance:latest"
readonly VERIQA_IMAGE="veriqa-authserver:latest"
readonly ADAPTER_IMAGE="veriqa-sample-whatsapp-twilio-adapter:latest"
readonly STAND_IMAGE="veriqa-sample-whatsapp-twilio-stand:latest"
readonly CHANNEL_TYPE="whatsapp-twilio"

# The instances of the adapter on the stand, whose logs must not carry a secret of the run, and the one the
# own checks of the tenants call, recreated by state.survives-recreate.
readonly ADAPTER_SERVICES=(adapter-suite adapter-checks adapter-tenants)
readonly RECREATED_SERVICE="adapter-checks"

# Upper bounds of every wait, in seconds.
readonly BUILD_TIMEOUT=1800
readonly START_TIMEOUT=300
readonly STAND_WAIT=120
readonly SUITE_TIMEOUT=900
readonly CHECKS_TIMEOUT=300
readonly CHANNEL_WAIT=240
readonly COMMAND_TIMEOUT=120
readonly MISSING_ENV_EXIT_CODE=2

readonly STAND_PROJECT="veriqa-wa-stand-$$"
readonly SAMPLE_PROJECT="veriqa-wa-sample-$$"
readonly SUITE_CONTAINER="$STAND_PROJECT-suite"

# The report outlives the run; the working directory, which holds the secrets, does not.
REPORT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/veriqa-whatsapp-twilio-report.XXXXXX")"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/veriqa-whatsapp-twilio-stand.XXXXXX")"
readonly REPORT_DIR WORK_DIR
readonly REPORT_PATH="$REPORT_DIR/report.json"

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
        docker rm -f "$SUITE_CONTAINER"
        stand_compose "$COMMAND_TIMEOUT" --profile checks --profile suite down -v --remove-orphans
        sample_compose "$COMMAND_TIMEOUT" down -v --remove-orphans
    } >/dev/null 2>&1
    rm -rf "$WORK_DIR"
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

# 32 random hexadecimal digits: an Auth Token, and the body of a SID after its two-letter prefix.
random_hex() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }

# A transport secret: whsec_ and the base64 of 32 random bytes.
transport_secret() { printf 'whsec_%s' "$(head -c 32 /dev/urandom | base64 | tr -d '\n')"; }

# A transaction id of Veriqa: 43 characters of Base62.
random_transaction() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 43; }

# Numbers in E.164: the user of the stand and one WhatsApp sender per account, all different.
number_base=$(($(random_number) % 9000000 + 1000000))
user="+1555$number_base"
default_from="+1444$((number_base + 1))"
shop_a_from="+1444$((number_base + 2))"
shop_b_from="+1444$((number_base + 3))"
sample_from="+1444$((number_base + 4))"

default_sid="AC$(random_hex)"
shop_a_sid="AC$(random_hex)"
shop_b_sid="AC$(random_hex)"
default_token="$(random_hex)"
shop_a_token="$(random_hex)"
shop_b_token="$(random_hex)"
stand_secret="$(transport_secret)"
# A string of the run the own checks put into adapter_settings; logs.no-secrets looks for it in the logs.
settings_marker="stand-settings-$(random_hex)"

(
    umask 077
    cat >"$WORK_DIR/stand.env" <<EOF
STAND_USER=$user
STAND_TRANSPORT_SECRET=$stand_secret
STAND_SETTINGS_MARKER=$settings_marker
STAND_DEFAULT_SID=$default_sid
STAND_DEFAULT_TOKEN=$default_token
STAND_DEFAULT_FROM=$default_from
STAND_DEFAULT_CONTENT_SID=HX$(random_hex)
STAND_SHOP_A_SID=$shop_a_sid
STAND_SHOP_A_TOKEN=$shop_a_token
STAND_SHOP_A_FROM=$shop_a_from
STAND_SHOP_A_CONTENT_SID=HX$(random_hex)
STAND_SHOP_B_SID=$shop_b_sid
STAND_SHOP_B_TOKEN=$shop_b_token
STAND_SHOP_B_FROM=$shop_b_from
STAND_SHOP_B_CONTENT_SID=HX$(random_hex)
STAND_ACCOUNTS=$default_sid:$default_token:$default_from,$shop_a_sid:$shop_a_token:$shop_a_from,$shop_b_sid:$shop_b_token:$shop_b_from
EOF
    cat >"$WORK_DIR/sample.env" <<EOF
VERIQA_TRANSPORT_SECRET=$(transport_secret)
TWILIO_ACCOUNT_SID=AC$(random_hex)
TWILIO_AUTH_TOKEN=$(random_hex)
TWILIO_WHATSAPP_FROM=$sample_from
TWILIO_PROMPT_CONTENT_SID=HX$(random_hex)
TWILIO_PUBLIC_URL=http://adapter:8000
EOF
)

# ── startup.missing-env: the image of the adapter without its transport secret ──
# Every other required variable holds a value of this check alone, so that the output can be searched for it.
missing_env_url="http://core-$(random_hex).example:8080"
missing_env_public_url="http://adapter-$(random_hex).example:8000"
missing_env_sid="AC$(random_hex)"
missing_env_token="$(random_hex)"
missing_env_from="+1444$((number_base + 5))"
missing_env_content_sid="HX$(random_hex)"
missing_env_output="$(timeout "$COMMAND_TIMEOUT" docker run --rm --network none \
    -e VERIQA_URL="$missing_env_url" \
    -e VERIQA_CHANNEL_TYPE="$CHANNEL_TYPE" \
    -e TWILIO_PUBLIC_URL="$missing_env_public_url" \
    -e TWILIO_ACCOUNT_SID="$missing_env_sid" \
    -e TWILIO_AUTH_TOKEN="$missing_env_token" \
    -e TWILIO_WHATSAPP_FROM="$missing_env_from" \
    -e TWILIO_PROMPT_CONTENT_SID="$missing_env_content_sid" \
    "$ADAPTER_IMAGE" 2>&1)"
missing_env_code=$?
missing_env_leaked=()
for value in "$missing_env_url" "$CHANNEL_TYPE" "$missing_env_public_url" "$missing_env_sid" "$missing_env_token" \
    "${missing_env_from#+}" "$missing_env_content_sid"; do
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

# ── 2. The stand and the conformance suite ───────────────────────────────────
show_stand_logs() {
    stand_compose "$COMMAND_TIMEOUT" logs --no-color --tail 60 "$@" 2>&1 | quote
}

# logs.no-secrets: the logs of every adapter of the stand, after the suite and the own checks, carry no
# secret of the run, no Auth Token, no adapter_settings and no number of the user in plain text. Only the
# adapters are read, not the stub.
check_logs() {
    local log="$WORK_DIR/adapter-logs.txt" name value
    local -a leaked=()
    local -A values=(
        ["the transport secret"]="$stand_secret"
        ["the key of the transport secret"]="${stand_secret#whsec_}"
        ["the Auth Token of the default record"]="$default_token"
        ["the Auth Token of shop-a"]="$shop_a_token"
        ["the Auth Token of shop-b"]="$shop_b_token"
        ["the adapter_settings of the own checks"]="$settings_marker"
        ["the number of the user of the stand"]="${user#+}"
    )
    if ! stand_compose "$COMMAND_TIMEOUT" logs --no-color --no-log-prefix "${ADAPTER_SERVICES[@]}" >"$log" 2>/dev/null || [ ! -s "$log" ]; then
        fail logs.no-secrets "the logs of the adapters of the stand could not be read"
        return
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
    if ! before="$(stand_compose "$COMMAND_TIMEOUT" run --rm -T checks node checks.mjs survive-before "$key" "$transaction")"; then
        fail state.survives-recreate "the call before the recreation did not go through"
    elif ! stand_compose "$START_TIMEOUT" up -d --force-recreate --no-deps "$RECREATED_SERVICE" >"$WORK_DIR/recreate.log" 2>&1; then
        tail -n 20 "$WORK_DIR/recreate.log" | quote
        fail state.survives-recreate "the container of $RECREATED_SERVICE was not recreated"
    elif ! stand_compose "$((STAND_WAIT + COMMAND_TIMEOUT))" run --rm -T checks node checks.mjs wait-stand "$STAND_WAIT"; then
        show_stand_logs "$RECREATED_SERVICE"
        fail state.survives-recreate "the recreated $RECREATED_SERVICE did not come up within the time allowed"
    elif ! stand_compose "$COMMAND_TIMEOUT" run --rm -T checks node checks.mjs survive-after "$key" "$transaction" "$before"; then
        fail state.survives-recreate "the repeat after the recreation did not find the state of the first call"
    else
        pass state.survives-recreate
    fi
}

stand_up=false
if stand_compose "$START_TIMEOUT" up -d >"$WORK_DIR/stand-up.log" 2>&1 \
    && stand_compose "$((STAND_WAIT + COMMAND_TIMEOUT))" run --rm -T checks node checks.mjs wait-stand "$STAND_WAIT"; then
    stand_up=true
    pass stand.up
else
    tail -n 40 "$WORK_DIR/stand-up.log" | quote
    show_stand_logs
    fail stand.up "the stub and the adapters did not come up within the time allowed"
fi

if $stand_up; then
    stand_compose "$SUITE_TIMEOUT" run -T --use-aliases --name "$SUITE_CONTAINER" suite 2>&1 | quote
    suite_code=${PIPESTATUS[0]}
    docker cp "$SUITE_CONTAINER:/tmp/report.json" "$REPORT_PATH" >/dev/null 2>&1

    if [ "$suite_code" -eq 2 ]; then
        suite_problem="the suite could not run (exit code 2): the stand is set up wrong, see its output above"
    elif [ ! -s "$REPORT_PATH" ]; then
        suite_problem="the suite left no report (exit code $suite_code)"
    elif ! stand_compose "$COMMAND_TIMEOUT" run --rm -T checks node checks.mjs suite-report <"$REPORT_PATH" \
        || [ "$suite_code" -ne 0 ]; then
        suite_problem="exit code $suite_code; the scenarios that do not match are listed above"
    else
        suite_problem=""
    fi
    if [ -z "$suite_problem" ]; then
        pass conformance.suite
    else
        show_stand_logs adapter-suite stub
        fail conformance.suite "$suite_problem"
    fi

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
    fail own.checks "not run: the stand is not up"
    fail logs.no-secrets "not run: the stand is not up"
    fail state.survives-recreate "not run: the stand is not up"
fi

stand_compose "$COMMAND_TIMEOUT" --profile checks --profile suite down -v --remove-orphans >/dev/null 2>&1

# ── 3. compose.yaml next to Veriqa ───────────────────────────────────────────
if sample_compose "$START_TIMEOUT" up -d >"$WORK_DIR/sample-up.log" 2>&1 \
    && sample_compose "$((CHANNEL_WAIT + COMMAND_TIMEOUT))" exec -T stub \
        node checks.mjs wait-channel http://veriqa:8080/health/channels "$CHANNEL_TYPE" "$CHANNEL_WAIT"; then
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
