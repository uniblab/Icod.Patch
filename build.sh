#!/usr/bin/env sh
set -eu

clean()
{
    printf '\n=== Clean ===\n'
    dotnet clean Icod.Patch.sln -c Debug
}

restore()
{
    printf '\n=== Restore ===\n'
    dotnet restore Icod.Patch.sln
}

build()
{
    printf '\n=== Build ===\n'
    dotnet build Icod.Patch.sln -c Debug --no-restore
}

case "${1-}" in
    "")
        clean
        restore
        build
        ;;

    clean)
        clean
        ;;

    restore)
        restore
        ;;

    build)
        build
        ;;

    *)
        printf 'Invalid section: %s\n' "$1" >&2
        printf 'Usage: %s [clean|restore|build]\n' "$0" >&2
        exit 1
        ;;
esac
