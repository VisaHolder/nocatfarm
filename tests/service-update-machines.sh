#!/bin/bash
# The reboots and power cuts of tests/service-update.sh, which take the machine down, so on machines of their own:
#
#   bash tests/service-update-machines.sh vm <zips>    Ubuntu in a throwaway QEMU VM: real reboots, and real power cuts (the
#                                                      kernel told to restart at once: no sync, no unmount, nothing stopped)
#   bash tests/service-update-machines.sh lxc <zips>   Debian in an unprivileged LXC container with systemd, the kind Proxmox
#                                                      makes: the whole test list, a reboot, and being killed from outside
#
# For a GitHub runner or another throwaway machine: needs root, and downloads the images.
set -u
KIND="$1"
ZIPS=$(cd "$2" && pwd)
SRC=$(cd "$(dirname "$0")/.." && pwd)
T="${RUNNER_TEMP:-/var/tmp}/nf-machines"
mkdir -p "$T"
REBOOTS="reboot_after_update power_cut_mid_swap power_cut_on_trial power_cut_while_staged"
results=""
failed=0

record() {   # PASS|FAIL, name
	results="$results
$1  $2"
	if [ "$1" = FAIL ]; then failed=$((failed + 1)); fi
}

if [ "$KIND" = vm ]; then
	IMG=noble-server-cloudimg-amd64.img
	PORT=2223
	sudo apt-get update -qq
	sudo apt-get install -y -qq qemu-system-x86 qemu-utils cloud-image-utils > /dev/null || { echo "couldn't install QEMU"; exit 1; }
	if [ -e /dev/kvm ]; then sudo chmod 666 /dev/kvm; ACCEL="-enable-kvm -cpu host"; else ACCEL=""; echo "no KVM here: this will be slow"; fi
	curl -sfL -o "$T/$IMG" "https://cloud-images.ubuntu.com/noble/current/$IMG" || { echo "couldn't download the image"; exit 1; }
	cp "$T/$IMG" "$T/disk.qcow2"
	qemu-img resize -q "$T/disk.qcow2" 12G
	ssh-keygen -q -t ed25519 -N "" -f "$T/key"
	cat > "$T/user-data" <<EOF
#cloud-config
users:
  - name: nf
    sudo: ALL=(ALL) NOPASSWD:ALL
    shell: /bin/bash
    ssh_authorized_keys: [$(cat "$T/key.pub")]
package_update: true
packages: [unzip, zip, curl, python3]
EOF
	cloud-localds "$T/seed.iso" "$T/user-data"
	# system_reset never needed: the guest's own kernel restarts at once, which is what a power cut leaves behind (cache=none:
	# what the guest hadn't sent to its disk is gone).
	boot() {
		qemu-system-x86_64 $ACCEL -m 3072 -smp 2 -nographic -drive "file=$T/disk.qcow2,if=virtio,cache=none" -drive "file=$T/seed.iso,if=virtio,format=raw" \
			-netdev "user,id=n0,hostfwd=tcp:127.0.0.1:$PORT-:22" -device virtio-net-pci,netdev=n0 > "$T/console.log" 2>&1 &
	}
	on() { ssh -i "$T/key" -p "$PORT" -o StrictHostKeyChecking=no -o UserKnownHostsFile=/dev/null -o ConnectTimeout=5 -o LogLevel=ERROR -o ServerAliveInterval=5 -o ServerAliveCountMax=3 nf@127.0.0.1 "$@"; }
	wait_up() {
		for _ in $(seq 1 120); do on true 2> /dev/null && return 0; sleep 5; done
		tail -n 30 "$T/console.log"
		return 1
	}
	boot
	wait_up || { echo "the VM never came up"; exit 1; }
	on 'cloud-init status --wait > /dev/null; systemctl is-system-running --wait; ps -p 1 -o comm=; systemctl --version | head -n 1; uname -r'
	tar -C "$SRC" -cf - tests/service-update.sh tools/linux src/NocatFarm/NocatFarm.csproj | on 'mkdir -p src && tar -xf - -C src'
	tar -C "$ZIPS" -cf - . | on 'mkdir -p zips && tar -xf - -C zips'
	for s in $REBOOTS; do
		echo "=========== $s"
		on "sudo bash src/tests/service-update.sh one ${s}_a zips"
		code=$?
		# 0 from a plain reboot's first half: this one restarts it. 255: the connection went with the power.
		if [ "$code" = 0 ]; then on 'sudo reboot' 2> /dev/null; elif [ "$code" != 255 ]; then record FAIL "$s (first half)"; continue; fi
		sleep 15
		wait_up || { record FAIL "$s: the machine never came back"; break; }
		if on "sudo bash src/tests/service-update.sh one ${s}_b zips"; then record PASS "$s"; else record FAIL "$s"; fi
	done
	on 'sudo poweroff' 2> /dev/null
elif [ "$KIND" = lxc ]; then
	NAME=nftest
	sudo apt-get update -qq
	sudo apt-get install -y -qq lxc lxc-templates uidmap dnsmasq-base > /dev/null || { echo "couldn't install LXC"; exit 1; }
	sudo iptables -P FORWARD ACCEPT
	sudo sysctl -q -w kernel.apparmor_restrict_unprivileged_userns=0 || true
	sudo systemctl restart lxc-net || true
	# Unprivileged, the way Proxmox makes them by default: root inside is an ordinary user outside.
	grep -q '^root:' /etc/subuid || echo 'root:100000:65536' | sudo tee -a /etc/subuid > /dev/null
	grep -q '^root:' /etc/subgid || echo 'root:100000:65536' | sudo tee -a /etc/subgid > /dev/null
	sudo tee "$T/lxc.conf" > /dev/null <<EOF
lxc.include = /etc/lxc/default.conf
lxc.idmap = u 0 100000 65536
lxc.idmap = g 0 100000 65536
EOF
	sudo DOWNLOAD_KEYSERVER=keyserver.ubuntu.com lxc-create -n "$NAME" -f "$T/lxc.conf" -t download -- -d debian -r bookworm -a amd64 > /dev/null || { echo "couldn't make the container"; exit 1; }
	sudo lxc-start -n "$NAME"
	on() { sudo lxc-attach -n "$NAME" --clear-env --keep-var TERM -v NF_TEST_POWER=host -v NF_SCENARIOS="${NF_SCENARIOS:-}" -v PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin -- "$@"; }
	wait_up() {
		for _ in $(seq 1 60); do on systemctl is-system-running 2> /dev/null | grep -qE 'running|degraded' && return 0; sleep 3; done
		return 1
	}
	wait_up || { echo "the container's systemd never came up"; on systemctl list-jobs; exit 1; }
	for _ in $(seq 1 30); do on getent hosts deb.debian.org > /dev/null && break; sleep 2; done
	echo "inside: PID 1 is $(on ps -p 1 -o comm= 2>/dev/null || on cat /proc/1/comm), container=$(on cat /proc/1/environ 2>/dev/null | tr '\0' '\n' | grep '^container=')"
	on sh -c 'grep -E "^0 " /proc/self/uid_map'
	on cat /etc/debian_version
	on sh -c 'apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq unzip zip curl python3 procps > /dev/null' || { echo "couldn't install the test tools"; exit 1; }
	echo "what a self-contained nocat.farm needs that's missing from this template:"
	on sh -c 'dpkg -l libssl3 libstdc++6 libgcc-s1 2>/dev/null | grep ^ii | cut -c5-30' || true
	tar -C "$SRC" -cf - tests/service-update.sh tools/linux src/NocatFarm/NocatFarm.csproj | on sh -c 'mkdir -p /root/src && tar -xf - -C /root/src'
	tar -C "$ZIPS" -cf - . | on sh -c 'mkdir -p /root/zips && tar -xf - -C /root/zips'
	on sh -c 'test -f /.dockerenv; echo ".dockerenv there: $?  (1 = no)"; env | grep -c DOTNET_RUNNING_IN_CONTAINER'
	if on bash /root/src/tests/service-update.sh all /root/zips; then record PASS "every scenario inside the container"; else record FAIL "scenarios inside the container"; fi
	for s in $REBOOTS; do
		echo "=========== $s (container)"
		if ! on bash /root/src/tests/service-update.sh one "${s}_a" /root/zips; then record FAIL "$s (first half)"; continue; fi
		if [ "$s" = reboot_after_update ]; then sudo lxc-stop -n "$NAME" -r -t 90; else sudo lxc-stop -n "$NAME" -k; sudo lxc-start -n "$NAME"; fi
		sleep 5
		wait_up || { record FAIL "$s: the container never came back"; continue; }
		if on bash /root/src/tests/service-update.sh one "${s}_b" /root/zips; then record PASS "$s"; else record FAIL "$s"; fi
	done
	sudo lxc-stop -n "$NAME" -k
else
	echo "vm or lxc"
	exit 2
fi

echo "== $KIND results:$results"
[ "$failed" = 0 ]
