#!/runtime/busybox sh
host_files=blocked
system_files=blocked
network=blocked
root_write=blocked
environment=cleared

if [ -e /mnt/d/DCx/AGENTS.md ] || [ -e /home ]; then host_files=exposed; fi
if [ -e /etc/passwd ] || [ -e /usr/bin ]; then system_files=exposed; fi
if /runtime/busybox wget -T 1 -qO- http://1.1.1.1/ >/dev/null 2>&1; then network=exposed; fi
if /runtime/busybox sh -c 'echo escape >/escape' 2>/dev/null; then root_write=exposed; fi
if [ -n "${DCX_TEST_SECRET:-}" ]; then environment=exposed; fi

printf 'host_files=%s system_files=%s network=%s root_write=%s environment=%s uid=%s gid=%s cpu=%s memory=%s processes=%s files=%s output=%s\n' \
    "$host_files" "$system_files" "$network" "$root_write" "$environment" \
    "$(/runtime/busybox id -u)" "$(/runtime/busybox id -g)" \
    "$(ulimit -t)" "$(ulimit -v)" "$(ulimit -p)" "$(ulimit -n)" "$(ulimit -f)"
