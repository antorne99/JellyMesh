"""Checks of the guards of run.sh, with stand-ins for docker, curl and df: no container is started."""
import os
import pathlib
import signal
import socket
import subprocess
import sys
import tempfile
import time
import unittest

HERE = pathlib.Path(__file__).resolve().parent
DOCKER = r"""#!/bin/sh
echo "$*" >>"$STUB/docker.log"
case $1 in
  inspect)
    [ -e "$STUB/deaf" ] && exec sleep 600
    [ -s "$STUB/misses" ] && { sed -i.bak 1d "$STUB/misses"; echo miss >>"$STUB/missed"; exit 1; }
    [ -e "$STUB/stopped" ] && state=false || state=true
    echo "$state $(cat "$STUB/started")" ;;
  exec) echo x ;;
  cp) case $2 in *:/config/log*) mkdir -p "$3"; echo "a log line" >"$3/log_1.log" ;; esac ;;
esac
"""
CURL = r"""#!/bin/sh
[ -e "$STUB/hang" ] && exec sleep 600
case "$*" in
  *AuthenticateByName*) echo '{"AccessToken":"token"}' ;;
  */System/Info/Public*) echo '{"Version":"12.4.0"}' ;;
  */ScheduledTasks*) echo '[{"Key":"JellyMeshIncrementalSync","Id":"i","State":"Idle"},{"Key":"JellyMeshReconciliation","Id":"n","State":"Idle"}]' ;;
  */Plugins*) echo '[{"Name":"JellyMesh","Status":"Active"}]' ;;
  */Edit/Check*) echo '{"Libraries":[{"Id":"l"}]}' ;;
  */Admin/Status*) echo '{"Servers":[{"Id":"00000000000000000000000000000001","Name":"scale","Url":"u"}]}' ;;
  */Admin/Servers*) echo '{"Id":"00000000000000000000000000000001"}' ;;
  */Admin/Sync*) echo '{"IsRunning":false,"LastStartedUtc":"2026-11-24T00:00:00Z","Servers":[]}' ;;
esac
"""
DF = r"""#!/bin/sh
echo "Filesystem 1024-blocks Used Available Capacity Mounted on"
echo "disk 100000000 1 $(cat "$STUB/free") 50% /"
"""


def free_port():
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


class GuardTest(unittest.TestCase):
    def setUp(self):
        self.folder = tempfile.TemporaryDirectory()
        self.addCleanup(self.folder.cleanup)
        self.root = pathlib.Path(self.folder.name)
        self.work = self.root / "work"
        stubs = self.root / "bin"
        stubs.mkdir()
        for name, text in (("docker", DOCKER), ("curl", CURL), ("df", DF)):
            (stubs / name).write_text(text)
            (stubs / name).chmod(0o755)
        install = self.root / "tree/tests/Integration/scripts/install-plugin.sh"
        install.parent.mkdir(parents=True)
        install.write_text("exit 0\n")
        (self.root / "started").write_text("2026-11-24T06:40:25Z")
        self.free_gib(100)
        self.env = dict(os.environ, PATH=f"{stubs}:{os.environ['PATH']}", STUB=str(self.root), WORK=str(self.work),
                        SCALE_TREE=str(self.root / "tree"), SCALE_NAME="jm-dry-run", SCALE_FAKE_PORT=str(free_port()),
                        SCALE_FILMS="1", SCALE_SERIES="1", SCALE_WATCH_S="0.2")
        for name in ("SCALE_REMOTES", "SCALE_TRACKS", "SCALE_SCAN", "SCALE_MIN_FREE_GIB", "KEEP"):
            self.env.pop(name, None)

    def free_gib(self, gib):
        (self.root / "free").write_text(str(int(gib * 1048576)))

    def start(self, *command, **env):
        process = subprocess.Popen(command, env=dict(self.env, **env), stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
                                   text=True, start_new_session=True)
        self.addCleanup(self.end, process)
        return process

    def end(self, process):
        try:
            os.killpg(process.pid, signal.SIGKILL)
        except (ProcessLookupError, PermissionError):
            pass
        process.wait()
        process.stdout.close()

    def wait_for(self, check, seconds=60):
        limit = time.time() + seconds
        while time.time() < limit:
            if check():
                return
            time.sleep(0.1)
        self.fail("timed out")

    def docker_log(self):
        log = self.root / "docker.log"
        return log.read_text() if log.exists() else ""


class FreeDiskTest(GuardTest):
    BIG = {"SCALE_SCAN": "1", "SCALE_REMOTES": "3", "SCALE_TRACKS": "100000"}

    def refused(self, **env):
        self.work.mkdir()
        (self.work / "results.txt").write_text("of the last run")
        process = self.start("bash", str(HERE / "run.sh"), **env)
        out, _ = process.communicate(timeout=20)
        self.assertEqual(1, process.returncode)
        self.assertEqual("", self.docker_log())
        self.assertEqual("of the last run", (self.work / "results.txt").read_text())
        return out

    def test_a_scanned_run_of_300000_tracks_needs_18_gib(self):
        self.free_gib(17.9)
        out = self.refused(**self.BIG)
        self.assertIn("17.9 GiB free, 18 needed", out)

    def test_a_number_that_is_asked_for_wins(self):
        self.free_gib(19)
        self.refused(SCALE_MIN_FREE_GIB="20")

    def test_a_disk_that_cannot_be_read_stops_the_run(self):
        (self.root / "free").write_text("-")
        self.refused(**self.BIG)

    def test_18_gib_are_enough_for_the_big_run(self):
        self.free_gib(18)
        self.start("bash", str(HERE / "run.sh"), **self.BIG)
        self.wait_for(lambda: "run -d" in self.docker_log())

    def test_a_small_run_asks_for_nothing(self):
        self.free_gib(1)
        self.start("bash", str(HERE / "run.sh"))
        self.wait_for(lambda: "run -d" in self.docker_log())


class ContainerFallTest(GuardTest):
    def running(self, **env):
        process = self.start("bash", str(HERE / "run.sh"), **env)
        self.wait_for((self.work / "first-requested").exists)
        return process

    def fallen(self, process, reason):
        out, _ = process.communicate(timeout=40)
        self.assertEqual(3, process.returncode, out)
        self.assertIn(reason, (self.work / "stopped.txt").read_text())
        self.assertIn(reason, out)
        self.assertEqual("a log line\n", (self.work / "jellyfin.log").read_text())
        self.assertTrue((self.work / "results-partial.txt").exists())
        self.assertFalse((self.work / "results.txt").exists())
        self.assertEqual(1, subprocess.run(["pgrep", "-f", str(self.work)], capture_output=True).returncode)

    def test_a_stopped_container_ends_the_run(self):
        process = self.running()
        (self.root / "stopped").touch()
        self.fallen(process, "stopped")
        self.assertIn("rm -f jm-dry-run", self.docker_log().split("run -d")[1])

    def test_a_restarted_container_ends_the_run(self):
        process = self.running(KEEP="1")
        (self.root / "started").write_text("2026-11-24T07:29:00Z")
        self.fallen(process, "restarted")
        self.assertNotIn("rm -f jm-dry-run", self.docker_log().split("run -d")[1])

    def test_a_call_that_hangs_does_not_keep_the_run_alive(self):
        process = self.running()
        (self.root / "hang").touch()
        time.sleep(3)
        (self.root / "stopped").touch()
        self.fallen(process, "stopped")

    def test_a_container_that_stays_up_does_not_end_the_run(self):
        process = self.running()
        (self.root / "misses").write_text("1\n2\n")
        self.wait_for(lambda: (self.root / "missed").exists() and len((self.root / "missed").read_text().split()) == 2)
        time.sleep(2)
        self.assertIsNone(process.poll())
        self.assertFalse((self.work / "stopped.txt").exists())

    def test_a_term_ends_a_run_whose_call_hangs_and_leaves_no_fake_or_watch(self):
        process = self.running()
        (self.root / "hang").touch()
        time.sleep(3)
        process.terminate()
        process.wait(timeout=10)
        self.wait_for(lambda: subprocess.run(["pgrep", "-f", f"fake_remote.py.*{self.work}|watch_container.py.*{self.work}"],
                                             capture_output=True).returncode == 1, 10)
        self.assertFalse((self.work / "stopped.txt").exists())
        self.assertIn("rm -f jm-dry-run", self.docker_log().split("run -d")[1])


class WatchTest(GuardTest):
    def watch(self, every="0.2", answer="0.5"):
        reason = self.root / "reason"
        return reason, self.start("bash", "-c", 'trap "exit 3" USR1; "$0" -I "$@" & sleep 600; exit 0', sys.executable,
                                  str(HERE / "watch_container.py"), "jm-dry-run", str(reason), every, answer)

    def test_a_docker_that_fails_three_times_in_a_row_counts_as_a_fall(self):
        reason, process = self.watch()
        self.wait_for(lambda: "inspect" in self.docker_log())
        (self.root / "misses").write_text("1\n2\n3\n4\n")
        self.assertEqual(3, process.wait(timeout=20))
        self.assertIn("did not answer", reason.read_text())

    def test_a_docker_that_hangs_counts_as_a_fall(self):
        reason, process = self.watch()
        self.wait_for(lambda: "inspect" in self.docker_log())
        (self.root / "deaf").touch()
        self.assertEqual(3, process.wait(timeout=20))
        self.assertIn("did not answer", reason.read_text())

    def test_the_watch_ends_with_the_run(self):
        reason, process = self.watch()
        self.wait_for(lambda: "inspect" in self.docker_log())
        process.kill()
        process.wait()
        self.wait_for(lambda: subprocess.run(["pgrep", "-f", str(reason)], capture_output=True).returncode == 1, 10)
        self.assertFalse(reason.exists())


if __name__ == "__main__":
    unittest.main()
