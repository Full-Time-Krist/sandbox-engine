#!/usr/bin/env python3
"""
Виртуальные устройства ввода для tools/playtest.sh: клавиатура, мышь и абсолютный указатель через /dev/uinput.

Нужен доступ на запись к /dev/uinput (пользователь в группе input). Только стандартная библиотека.

Почему не ydotool: его единое устройство «всё сразу» udev определяет как клавиатуру, и Wayland-композитор
не принимает от него кнопки и движение мыши; а «абсолютное» перемещение ydotool — это относительное
движение через ускорение указателя, и курсор встаёт неточно. Здесь три отдельных устройства:
  - клавиатура;
  - мышь (относительное движение, кнопки, колесо): поворот камеры в захваченном режиме и клики;
  - абсолютный указатель (как планшет QEMU): курсор ставится в точку экрана без ускорения.

Запуск демоном: vinput.py daemon FIFO WIDTH HEIGHT — WIDTH×HEIGHT задают диапазон абсолютных координат,
это размер экрана в логических пикселях (композитор растягивает диапазон на экран).
Команды — строки в FIFO:
  key CODE 1|0          клавиша (коды linux/input-event-codes.h)
  btn left|right|middle 1|0
  rel DX DY             относительное движение мыши
  wheel N               колесо: +1 — от себя
  abs X Y               курсор в точку экрана
  quit
"""
import fcntl
import os
import struct
import sys
import time

UI_SET_EVBIT = 0x40045564
UI_SET_KEYBIT = 0x40045565
UI_SET_RELBIT = 0x40045566
UI_SET_ABSBIT = 0x40045567
UI_DEV_CREATE = 0x5501
UI_DEV_DESTROY = 0x5502

EV_SYN, EV_KEY, EV_REL, EV_ABS = 0x00, 0x01, 0x02, 0x03
SYN_REPORT = 0
REL_X, REL_Y, REL_WHEEL = 0x00, 0x01, 0x08
ABS_X, ABS_Y = 0x00, 0x01
BUTTONS = {"left": 0x110, "right": 0x111, "middle": 0x112}
BUS_VIRTUAL = 0x06
ABS_CNT = 64


class Device:
    def __init__(self, name, keys=(), rel=(), absolute=None):
        self.fd = os.open("/dev/uinput", os.O_WRONLY | os.O_NONBLOCK)
        if keys:
            fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_KEY)
            for code in keys:
                fcntl.ioctl(self.fd, UI_SET_KEYBIT, code)
        if rel:
            fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_REL)
            for code in rel:
                fcntl.ioctl(self.fd, UI_SET_RELBIT, code)
        absmax = [0] * ABS_CNT
        if absolute:
            fcntl.ioctl(self.fd, UI_SET_EVBIT, EV_ABS)
            for code, maximum in absolute.items():
                fcntl.ioctl(self.fd, UI_SET_ABSBIT, code)
                absmax[code] = maximum
        # struct uinput_user_dev: name[80], input_id (bustype, vendor, product, version), ff_effects_max,
        # absmax/absmin/absfuzz/absflat по ABS_CNT значений.
        setup = struct.pack("80sHHHHi", name.encode(), BUS_VIRTUAL, 0x1234, 0x5678, 1, 0)
        setup += struct.pack(f"{ABS_CNT}i", *absmax) + bytes(4 * ABS_CNT * 3)
        os.write(self.fd, setup)
        fcntl.ioctl(self.fd, UI_DEV_CREATE)

    def emit(self, *events):
        data = b""
        for ev_type, code, value in (*events, (EV_SYN, SYN_REPORT, 0)):
            now = time.time()
            data += struct.pack("llHHi", int(now), int(now % 1 * 1_000_000), ev_type, code, value)
        os.write(self.fd, data)

    def close(self):
        fcntl.ioctl(self.fd, UI_DEV_DESTROY)
        os.close(self.fd)


def daemon(fifo, width, height):
    keyboard = Device("playtest keyboard", keys=range(1, 249))
    mouse = Device("playtest mouse", keys=BUTTONS.values(), rel=(REL_X, REL_Y, REL_WHEEL))
    # Абсолютному указателю тоже нужна кнопка: без неё udev не считает его мышью.
    pointer = Device("playtest pointer", keys=(BUTTONS["left"],), absolute={ABS_X: width - 1, ABS_Y: height - 1})
    # Композитор подхватывает новые устройства не мгновенно.
    time.sleep(0.5)
    print("ready", flush=True)
    try:
        while True:
            with open(fifo) as commands:
                for line in commands:
                    parts = line.split()
                    if not parts:
                        continue
                    command, args = parts[0], parts[1:]
                    if command == "key":
                        keyboard.emit((EV_KEY, int(args[0]), int(args[1])))
                    elif command == "btn":
                        mouse.emit((EV_KEY, BUTTONS[args[0]], int(args[1])))
                    elif command == "rel":
                        mouse.emit((EV_REL, REL_X, int(args[0])), (EV_REL, REL_Y, int(args[1])))
                    elif command == "wheel":
                        mouse.emit((EV_REL, REL_WHEEL, int(args[0])))
                    elif command == "abs":
                        pointer.emit((EV_ABS, ABS_X, int(args[0])), (EV_ABS, ABS_Y, int(args[1])))
                    elif command == "quit":
                        return
                    else:
                        print(f"неизвестная команда: {line.strip()}", file=sys.stderr, flush=True)
    finally:
        for device in (keyboard, mouse, pointer):
            device.close()


if __name__ == "__main__":
    if len(sys.argv) != 5 or sys.argv[1] != "daemon":
        sys.exit(__doc__)
    daemon(sys.argv[2], int(sys.argv[3]), int(sys.argv[4]))
