#!/bin/sh
# Playground entrypoint, see CONTRACT.md in the playground-runner repo.
set -u

# fluke writes to ./target and prints source paths as given, so build next to a copy of the sources
# to get diagnostics like main.fl:3:5
cd /work/build
cp -R /work/src/. .

# fluke build reports progress on stdout; only show it if the build fails
fluke build "$PLAYGROUND_ENTRY" > /tmp/build.log
status=$?
[ "$status" -eq 0 ] || cat /tmp/build.log
case $status in
    0) ;;
    3) exit 100 ;; # compile error
    *) exit 101 ;; # I/O, usage, internal compiler error, anything unexpected
esac

# fluke names the assembly after the entry file, without extension
dll="/work/build/target/$(basename "${PLAYGROUND_ENTRY%.*}").dll"
[ -f "$dll" ] || { echo "playground: expected assembly at $dll" >&2; exit 101; }

printf '\036PLAYGROUND-RUN\036\n'
printf '\036PLAYGROUND-RUN\036\n' >&2
exec dotnet "$dll" < /work/stdin
