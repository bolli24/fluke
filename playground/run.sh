#!/bin/sh
# Playground entrypoint, see CONTRACT.md in the playground-runner repo.
set -u

# Runners from before views don't set these
views=${PLAYGROUND_VIEWS:-}
run=${PLAYGROUND_RUN:-1}

# fluke writes to ./target and prints source paths as given, so build next to a copy of the sources
# to get diagnostics like main.fl:3:5
cd /work/build
cp -R /work/src/. .

# fluke build reports progress on stdout; only show it if the build fails
fluke build "$PLAYGROUND_ENTRY" --emit "$views" --emit-dir /tmp/views > /tmp/build.log
status=$?
[ "$status" -eq 0 ] || cat /tmp/build.log

# Views come after the compiler's output, also on a failed build, as far as it got. They can contain
# strings from the program, so \x1e is removed to keep them from faking a header or the marker.
for view in $(echo "$views" | tr ',' ' '); do
    [ -f "/tmp/views/$view.txt" ] || continue
    printf '\036PLAYGROUND-VIEW %s\036\n' "$view"
    tr -d '\036' < "/tmp/views/$view.txt" | head -c 262145
done

case $status in
    0) ;;
    3) exit 100 ;; # compile error
    *) exit 101 ;; # I/O, usage, internal compiler error, anything unexpected
esac
[ "$run" = 1 ] || exit 0

# fluke names the assembly after the entry file, without extension
dll="/work/build/target/$(basename "${PLAYGROUND_ENTRY%.*}").dll"
[ -f "$dll" ] || { echo "playground: expected assembly at $dll" >&2; exit 101; }

printf '\036PLAYGROUND-RUN\036\n'
printf '\036PLAYGROUND-RUN\036\n' >&2
exec dotnet "$dll" < /work/stdin
