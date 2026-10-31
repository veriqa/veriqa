#!/usr/bin/env bash
# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
#
# CI gate of the reference WhatsApp adapter through Baileys — the one entry point, run from anywhere:
#
#   bash samples/node/custom-channel-whatsapp-baileys/ci/run.sh
#
# 1. Builds the images: the conformance suite, Veriqa, the adapter and the stand; starts the image of the
#    adapter without its transport secret, which must stop it with exit code 2 (startup.missing-env).
# 2. Starts the stand (ci/compose.stand.yaml) and runs the conformance suite in the automatic mode against
#    the adapter in the stub mode; every scenario must pass but platform.cannot-continue, which must be not
#    applicable — a personal number has no case in which the platform cannot carry a confirmation out.
# 3. Runs the own checks of the adapter (ci/checks.mjs) against the recorder of the platform stub; then reads
#    the logs of the adapters of the stand for the secrets of the run (logs.no-secrets), and recreates the
#    container of the adapter of the checks between a call and its repeat (state.survives-recreate).
# 4. Starts compose.yaml with the CI overlay and waits until Veriqa reports the channel healthy.
#
# Prints one "PASS|FAIL <check>" line per step and, as the last line, the path of the JSON report of the
# suite. The transport secrets of the run are generated into a temporary directory outside the repository
# and removed with it; the numbers are fictional (555-01xx), and the network of the stand has no way out.
# The stand is stopped whatever the outcome. Exit code 0 only when every step passed.

set -uo pipefail

SAMPLE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REPO_ROOT="$(cd "$SAMPLE_DIR/../../.." && pwd)"
readonly SAMPLE_DIR REPO_ROOT

readonly SUITE_IMAGE="veriqa-http-channel-conformance:latest"
readonly VERIQA_IMAGE="veriqa-authserver:latest"
readonly ADAPTER_IMAGE="veriqa-sample-whatsapp-baileys-adapter:latest"
readonly STAND_IMAGE="veriqa-sample-whatsapp-baileys-stand:latest"
readonly CHANNEL_TYPE="whatsapp-baileys"

# The instances of the adapter on the stand, whose logs must not carry a secret of the run, and the one the
# own checks of the tenants call, recreated by state.survives-recreate.
readonly ADAPTER_SERVICES=(adapter-suite adapter-checks adapter-baileys adapter-tenants)
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

readonly STAND_PROJECT="veriqa-wab-stand-$$"
readonly SAMPLE_PROJECT="veriqa-wab-sample-$$"
readonly SUITE_CONTAINER="$STAND_PROJECT-suite"

# Fictional numbers in E.164: the 555-0100 to 555-0199 range is reserved for fiction, so none of them is a
# WhatsApp account — the instance in the baileys mode could not reach one even with a way out.
readonly STAND_USER="+12025550100"
readonly STAND_STRANGER="+12025550199"
readonly DEFAULT_NUMBER="+12025550101"
readonly SHOP_A_NUMBER="+12025550102"
readonly SHOP_B_NUMBER="+12025550103"
readonly BAILEYS_NUMBER="+12025550150"
readonly SAMPLE_NUMBER="+12025550160"

# Labels of the records: the credentials of the platform log, different from the names of the tenants.
readonly DEFAULT_LABEL="stand-default"
readonly SHOP_A_LABEL="stand-shop-a"
readonly SHOP_B_LABEL="stand-shop-b"
readonly BAILEYS_LABEL="stand-offline"

# The report outlives the run; the working directory, which holds the secrets, does not.
REPORT_DIR="$(mktemp -d "${TMPDIR:-/tmp}/veriqa-whatsapp-baileys-report.XXXXXX")"
WORK_DIR="$(mktemp -d "${TMPDIR:-/tmp}/veriqa-whatsapp-baileys-stand.XXXXXX")"
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
# A transport secret: whsec_ and the base64 of 32 random bytes.
transport_secret() { printf 'whsec_%s' "$(head -c 32 /dev/urandom | base64 | tr -d '\n')"; }

# 32 random hexadecimal digits: markers of the run and idempotency keys.
random_hex() { od -An -N16 -tx1 /dev/urandom | tr -d ' \n'; }

# A transaction id of Veriqa: 43 characters of Base62.
random_transaction() { LC_ALL=C tr -dc 'A-Za-z0-9' </dev/urandom | head -c 43; }

stand_secret="$(transport_secret)"
# A string of the run the own checks put into adapter_settings; logs.no-secrets looks for it in the logs.
settings_marker="stand-settings-$(random_hex)"

(
    umask 077
    cat >"$WORK_DIR/stand.env" <<ENV
STAND_TRANSPORT_SECRET=$stand_secret
STAND_SETTINGS_MARKER=$settings_marker
STAND_USER=$STAND_USER
STAND_STRANGER=$STAND_STRANGER
STAND_DEFAULT_LABEL=$DEFAULT_LABEL
STAND_DEFAULT_NUMBER=$DEFAULT_NUMBER
STAND_SHOP_A_LABEL=$SHOP_A_LABEL
STAND_SHOP_A_NUMBER=$SHOP_A_NUMBER
STAND_SHOP_B_LABEL=$SHOP_B_LABEL
STAND_SHOP_B_NUMBER=$SHOP_B_NUMBER
STAND_BAILEYS_LABEL=$BAILEYS_LABEL
STAND_BAILEYS_NUMBER=$BAILEYS_NUMBER
STAND_RECORDS=$DEFAULT_LABEL:$DEFAULT_NUMBER,$SHOP_A_LABEL:$SHOP_A_NUMBER,$SHOP_B_LABEL:$SHOP_B_NUMBER
ENV
    cat >"$WORK_DIR/sample.env" <<ENV
VERIQA_TRANSPORT_SECRET=$(transport_secret)
WHATSAPP_NUMBER=$SAMPLE_NUMBER
ENV
)

# ── startup.missing-env: the image of the adapter without its transport secret ──
# Every other required variable holds a value of this check alone, so that the output can be searched for it.
# The stub mode keeps the adapter from reaching for WhatsApp Web, and it has no network either.
missing_env_url="http://core-$(random_hex).example:8080"
missing_env_stub_url="http://stub-$(random_hex).example:9000"
missing_env_label="missing-env-$(random_hex)"
missing_env_number="+12025550170"
missing_env_output="$(timeout "$COMMAND_TIMEOUT" docker run --rm --network none \
    -e VERIQA_URL="$missing_env_url" \
    -e VERIQA_CHANNEL_TYPE="$CHANNEL_TYPE" \
    -e WHATSAPP_PLATFORM=stub \
    -e WHATSAPP_STUB_URL="$missing_env_stub_url" \
    -e WHATSAPP_LABEL="$missing_env_label" \
    -e WHATSAPP_NUMBER="$missing_env_number" \
    "$ADAPTER_IMAGE" 2>&1)"
missing_env_code=$?
missing_env_leaked=()
for value in "$missing_env_url" "$CHANNEL_TYPE" "$missing_env_stub_url" "$missing_env_label" "${missing_env_number#+}"; do
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
# secret of the run, no adapter_settings and no number of the user in plain text. Only the adapters are
# read, not the stub.
check_logs() {
    local log="$WORK_DIR/adapter-logs.txt" name value
    local -a leaked=()
    local -A values=(
        ["the transport secret"]="$stand_secret"
        ["the key of the transport secret"]="${stand_secret#whsec_}"
        ["the adapter_settings of the own checks"]="$settings_marker"
        ["the number of the user of the stand"]="${STAND_USER#+}"
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
        show_stand_logs adapter-checks adapter-baileys stub
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
