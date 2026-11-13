#!/usr/bin/env python3
"""Serial ports for the CI tests, made of pseudo-terminals (which System.IO.Ports opens like
any serial device).

  pty-bridge.py loopback <file>   one port that sends back whatever it receives
  pty-bridge.py bridge <file>     two ports joined to each other (the SITL on one,
                                  Mission Planner on the other)

Writes the port path(s) to <file>, one per line, then relays until killed.
"""
import os
import select
import sys
import tty


def open_port():
    master, slave = os.openpty()
    tty.setraw(slave)  # no echo, no line editing: a byte stream, like a serial line
    return master, os.ttyname(slave), slave


def main():
    mode, out = sys.argv[1], sys.argv[2]
    ports = [open_port() for _ in range(1 if mode == "loopback" else 2)]
    with open(out + ".tmp", "w") as f:
        f.write("".join(path + "\n" for _, path, _ in ports))
    os.replace(out + ".tmp", out)
    masters = [m for m, _, _ in ports]
    # (the slave ends stay open here too: a pty whose slave nobody holds reports EOF/EIO to the
    # master as soon as the program on the other side closes and reopens the port)
    while True:
        readable, _, _ = select.select(masters, [], [])
        for m in readable:
            try:
                data = os.read(m, 4096)
            except OSError:
                continue
            target = m if mode == "loopback" else masters[1 - masters.index(m)]
            os.write(target, data)


if __name__ == "__main__":
    main()
