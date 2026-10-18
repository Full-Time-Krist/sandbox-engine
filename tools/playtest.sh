#!/usr/bin/env bash
# Помощник для проверки игры вживую под Linux (Wayland + niri): запуск, имитация ввода, скриншоты, лог.
# Аналог tools/playtest.ps1. Каждая команда — отдельный вызов: состояние (PID игры, лог) хранится
# в $XDG_RUNTIME_DIR/sandbox-playtest, поэтому вызовы можно делать из разных процессов shell.
#
#   tools/playtest.sh start                     # Debug-сборка, мир 'playtest', каждый раз новый (сид 1)
#   tools/playtest.sh start --release           # Release — для замеров
#   tools/playtest.sh start --keep-world        # загрузить сохранённый мир (проверка сохранений)
#   tools/playtest.sh start -- --seed 2 --spawn 1000000 0 --mode creative   # аргументы игры — после --
#   tools/playtest.sh start -- --mods examples/ExampleMod/bin/Debug        # с модами (по умолчанию — пустая папка модов)
#   tools/playtest.sh enter                     # клик в центр кадра: захват мыши
#   tools/playtest.sh key w 1000                # W одну секунду
#   tools/playtest.sh keys 1500 w space         # W и Space вместе (разбег с прыжком)
#   tools/playtest.sh key-down shift            # зажать клавишу (Shift+клик в инвентаре); key-up shift — отпустить
#   tools/playtest.sh move 0 10 30              # 30 шагов по 10 px вниз (относительное движение)
#   tools/playtest.sh mouse-to 400 300          # курсор в точку кадра (для экрана инвентаря)
#   tools/playtest.sh click left|right          # клик; drag-to X Y left|right — перетаскивание до точки
#   tools/playtest.sh hold left 1500            # держать кнопку мыши (ломание удержанием); hold-start/hold-end — без ожидания
#   tools/playtest.sh scroll -1                 # колесо: +1 — от себя, -1 — на себя
#   tools/playtest.sh resize 800 600            # размер кадра, например для масштаба интерфейса
#   tools/playtest.sh shot /path/frame.png      # только кадр, пиксель в пиксель (даже если окно перекрыто)
#   tools/playtest.sh title                     # заголовок: FPS, TPS, строка состояния
#   tools/playtest.sh log                       # лог (stdout и stderr) запущенной игры
#   tools/playtest.sh stop                      # закрыть окно, дождаться сохранения, вывести лог
#
# Нужны: niri (управление окнами и скриншоты), jq, python3 и запись в /dev/uinput (пользователь в группе input).
# Ввод идёт через tools/vinput.py — виртуальные клавиатуру, мышь и абсолютный указатель (почему не ydotool —
# в начале vinput.py). Его демон запускается при start и останавливается при stop.
#
# Окно игры делается плавающим: так его размер задаётся точно, а положение известно (для mouse-to).
# В niri включён focus-follows-mouse: пока идёт проверка, курсор должен оставаться над окном игры.
# Ввод уходит в окно с фокусом. Если пользователь в это время работает мышью, ввод попадёт не туда.
set -euo pipefail

ROOT=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
STATE=${XDG_RUNTIME_DIR:-/tmp}/sandbox-playtest
mkdir -p "$STATE"
FIFO=$STATE/vinput.fifo

# Название игры (и имя исполняемого файла, и папка сохранений) — GameName в Directory.Build.props.
GAME_NAME=$(sed -n 's:.*<GameName>\(.*\)</GameName>.*:\1:p' "$ROOT/Directory.Build.props" | head -n1)
GAME_NAME=${GAME_NAME:-Sandbox}

die() { echo "playtest: $*" >&2; exit 1; }

pid() {
    [[ -f $STATE/pid ]] || die "игра не запущена (нет $STATE/pid)"
    local p; p=$(<"$STATE/pid")
    kill -0 "$p" 2>/dev/null || die "игра (PID $p) уже завершилась; лог: tools/playtest.sh log"
    echo "$p"
}

# Окно niri по PID игры: JSON или пусто, если окна ещё нет.
window_json() { niri msg -j windows | jq -c --argjson pid "$1" 'first(.[] | select(.pid == $pid)) // empty'; }
window_id() { window_json "$(pid)" | jq -r '.id'; }

# Левый верхний угол кадра на экране (логические пиксели): позиция плавающего окна в рабочем столе,
# отступ окна внутри плитки и позиция монитора.
frame_origin() {
    local win output
    win=$(window_json "$(pid)")
    [[ -n $win ]] || die "окно игры не найдено"
    output=$(niri msg -j workspaces | jq -r --argjson ws "$(jq '.workspace_id' <<<"$win")" '.[] | select(.id == $ws) | .output')
    niri msg -j outputs | jq -r --arg out "$output" --argjson win "$win" '
        .[$out].logical as $o
        | ($win.layout.tile_pos_in_workspace_view // error("окно не плавающее: позиция неизвестна")) as $t
        | "\(($o.x + $t[0] + $win.layout.window_offset_in_tile[0]) | floor) \(($o.y + $t[1] + $win.layout.window_offset_in_tile[1]) | floor)"'
}

vinput_running() { [[ -f $STATE/vinput.pid && -p $FIFO ]] && kill -0 "$(<"$STATE/vinput.pid")" 2>/dev/null; }

# Демон виртуальных устройств. Диапазон абсолютного указателя — весь экран (все мониторы) в логических пикселях.
ensure_vinput() {
    vinput_running && return
    local size
    size=$(niri msg -j outputs | jq -r '[.[] | .logical | select(. != null)]
        | "\(map(.x + .width) | max) \(map(.y + .height) | max)"')
    rm -f "$FIFO"; mkfifo "$FIFO"
    # shellcheck disable=SC2086
    setsid python3 -I "$ROOT/tools/vinput.py" daemon "$FIFO" $size >"$STATE/vinput.log" 2>&1 </dev/null &
    echo $! >"$STATE/vinput.pid"
    for _ in {1..50}; do grep -q '^ready' "$STATE/vinput.log" 2>/dev/null && return; sleep 0.1; done
    die "vinput.py не запустился: $(cat "$STATE/vinput.log")"
}

send() { ensure_vinput; echo "$*" >"$FIFO"; }

# Коды клавиш Linux (linux/input-event-codes.h). Неизвестное имя можно передать числом.
keycode() {
    case ${1,,} in
        esc|escape) echo 1 ;; [1-9]) echo $(($1 + 1)) ;; 0) echo 11 ;;
        q) echo 16 ;; w) echo 17 ;; e) echo 18 ;; r) echo 19 ;; t) echo 20 ;;
        y) echo 21 ;; u) echo 22 ;; i) echo 23 ;; o) echo 24 ;; p) echo 25 ;;
        a) echo 30 ;; s) echo 31 ;; d) echo 32 ;; f) echo 33 ;; g) echo 34 ;;
        h) echo 35 ;; j) echo 36 ;; k) echo 37 ;; l) echo 38 ;;
        z) echo 44 ;; x) echo 45 ;; c) echo 46 ;; v) echo 47 ;; b) echo 48 ;; n) echo 49 ;; m) echo 50 ;;
        ctrl|lctrl) echo 29 ;; shift|lshift) echo 42 ;; rshift) echo 54 ;; alt) echo 56 ;;
        space) echo 57 ;; enter) echo 28 ;; tab) echo 15 ;; f3) echo 61 ;;
        [0-9]*) echo "$1" ;;
        *) die "неизвестная клавиша '$1' (можно указать код из linux/input-event-codes.h)" ;;
    esac
}

ms() { sleep "$(awk "BEGIN { print $1 / 1000 }")"; }

cmd_start() {
    local config=Debug world=playtest keep=0 width=1280 height=720 wait=3 args=()
    while (($#)); do
        case $1 in
            --release) config=Release ;;
            --keep-world) keep=1 ;;
            --world) world=$2; shift ;;
            --size) width=${2%x*}; height=${2#*x}; shift ;;
            --wait) wait=$2; shift ;;
            --) shift; args=("$@"); break ;;
            *) die "неизвестный параметр '$1'" ;;
        esac
        shift
    done

    if [[ -f $STATE/pid ]] && kill -0 "$(<"$STATE/pid")" 2>/dev/null; then
        die "игра уже запущена (PID $(<"$STATE/pid")); сначала tools/playtest.sh stop"
    fi

    local exe=$ROOT/src/Client/bin/$config/net10.0/$GAME_NAME
    [[ -x $exe ]] || die "нет $exe — сначала dotnet build Game.slnx${config:+ -c $config}"

    # Мир: по умолчанию отдельный 'playtest', а не мир игрока. Без --keep-world он удаляется перед запуском.
    local world_dir=${XDG_DATA_HOME:-$HOME/.local/share}/$GAME_NAME/Saves/$world
    ((keep)) || rm -rf "$world_dir"
    [[ " ${args[*]} " == *" --seed "* ]] || args=(--seed 1 "${args[@]}")
    # Моды: по умолчанию пустая папка, а не общая папка модов игрока. Свою — аргументом игры: -- --mods ПАПКА.
    # Игра запускается из $STATE, поэтому относительный путь к модам переводим в абсолютный от текущей папки.
    local i
    for ((i = 0; i < ${#args[@]} - 1; i++)); do
        [[ ${args[i]} == --mods ]] && args[i + 1]=$(realpath -m "${args[i + 1]}")
    done
    if [[ " ${args[*]} " != *" --mods "* ]]; then
        mkdir -p "$STATE/mods"
        args=(--mods "$STATE/mods" "${args[@]}")
    fi

    ensure_vinput
    : >"$STATE/out.txt"; : >"$STATE/err.txt"
    (cd "$STATE" && exec setsid "$exe" --world "$world" "${args[@]}" >"$STATE/out.txt" 2>"$STATE/err.txt" </dev/null) &
    local game=$!
    echo "$game" >"$STATE/pid"

    local win=
    for _ in $(seq $((wait * 10))); do
        kill -0 "$game" 2>/dev/null || die "игра завершилась при запуске: $(cat "$STATE/err.txt")"
        win=$(window_json "$game")
        [[ -n $win ]] && break
        sleep 0.1
    done
    [[ -n $win ]] || die "окно игры не появилось за $wait с"

    local id; id=$(jq '.id' <<<"$win")
    [[ $(jq '.is_floating' <<<"$win") == true ]] || niri msg action toggle-window-floating --id "$id"
    niri msg action set-window-width --id "$id" "$width"
    niri msg action set-window-height --id "$id" "$height"
    niri msg action move-floating-window --id "$id" -x 40 -y 40
    niri msg action focus-window --id "$id"
    sleep 0.5
    # Курсор — в центр кадра, дважды: первое событие нового абсолютного устройства композитор не применяет.
    # Заодно курсор оказывается над окном игры (focus-follows-mouse).
    cmd_mouse_to $((width / 2)) $((height / 2))
    cmd_mouse_to $((width / 2)) $((height / 2))
    echo "Игра запущена: PID $game, окно $id, мир '$world', $config. Лог: $STATE/out.txt"
}

cmd_mouse_to() {
    local x=$1 y=$2 ox oy
    read -r ox oy < <(frame_origin)
    send abs $((ox + x)) $((oy + y))
    sleep 0.15
}

cmd_click() {
    local button=${1:-left}
    [[ $button == left || $button == right ]] || die "кнопка: left или right"
    send btn "$button" 1; sleep 0.03; send btn "$button" 0
    sleep 0.3
}

cmd_enter() {
    local size
    size=$(window_json "$(pid)" | jq -r '.layout.window_size | "\(.[0] / 2 | floor) \(.[1] / 2 | floor)"')
    niri msg action focus-window --id "$(window_id)"
    # shellcheck disable=SC2086
    cmd_mouse_to $size
    cmd_click left
}

cmd_key() {
    local code; code=$(keycode "$1")
    send key "$code" 1; ms "${2:-50}"; send key "$code" 0
    sleep 0.2
}

cmd_keys() {
    local hold=$1; shift
    local codes=() c
    for c in "$@"; do codes+=("$(keycode "$c")"); done
    for c in "${codes[@]}"; do send key "$c" 1; done
    ms "$hold"
    for c in "${codes[@]}"; do send key "$c" 0; done
    sleep 0.2
}

# Относительное движение: в захваченном режиме игра получает его как сырой ввод.
cmd_move() {
    local dx=$1 dy=$2 steps=${3:-1}
    for _ in $(seq "$steps"); do send rel "$dx" "$dy"; sleep 0.015; done
    sleep 0.3
}

# Держать кнопку мыши: ломание удержанием ЛКМ. hold-start/hold-end — нажать и отпустить отдельно (скриншот посередине).
cmd_hold() {
    local button=${1:-left}
    [[ $button == left || $button == right ]] || die "кнопка: left или right"
    send btn "$button" 1; ms "${2:-1000}"; send btn "$button" 0
    sleep 0.2
}

# Нажать кнопку, провести курсор до точки кадра и отпустить: перетаскивание по слотам инвентаря.
cmd_drag_to() {
    local x=$1 y=$2 button=${3:-left}
    [[ $button == left || $button == right ]] || die "кнопка: left или right"
    send btn "$button" 1; sleep 0.1
    cmd_mouse_to "$x" "$y"; sleep 0.1
    send btn "$button" 0; sleep 0.3
}

cmd_scroll() {
    send wheel "$1"
    sleep 0.3
}

cmd_resize() {
    local id; id=$(window_id)
    niri msg action set-window-width --id "$id" "$1"
    niri msg action set-window-height --id "$id" "$2"
    sleep 0.7
}

# Снимок окна средствами niri: только кадр, без рамки и того, что поверх. niri пишет файл асинхронно.
cmd_shot() {
    local path; path=$(realpath -m "$1")
    rm -f "$path"
    niri msg action screenshot-window --id "$(window_id)" --write-to-disk true --path "$path" >/dev/null
    for _ in {1..50}; do [[ -s $path ]] && { sleep 0.1; echo "$path"; return; }; sleep 0.1; done
    die "скриншот не появился: $path"
}

cmd_title() { window_json "$(pid)" | jq -r '.title'; }

cmd_log() {
    echo '--- stdout ---'; cat "$STATE/out.txt" 2>/dev/null || true
    echo '--- stderr ---'; cat "$STATE/err.txt" 2>/dev/null || true
}

cmd_stop() {
    [[ -f $STATE/pid ]] || die "игра не запущена"
    local game; game=$(<"$STATE/pid")
    local alive=no
    if kill -0 "$game" 2>/dev/null; then
        alive=yes
        # Закрытие окна, а не kill: при выходе игра дописывает сохранение. Ждём завершения, а не фиксированное время.
        local id; id=$(window_json "$game" | jq -r '.id // empty')
        if [[ -n $id ]]; then niri msg action close-window --id "$id"; else kill -TERM "$game"; fi
        for _ in {1..150}; do kill -0 "$game" 2>/dev/null || break; sleep 0.1; done
    fi
    local exited=yes
    kill -0 "$game" 2>/dev/null && exited=no
    echo "alive до закрытия: $alive; завершилась: $exited"
    [[ $exited == yes ]] && rm -f "$STATE/pid"
    # Виртуальные устройства больше не нужны.
    vinput_running && echo quit >"$FIFO"
    cmd_log
}

command=${1:-}
shift || true
case $command in
    start) cmd_start "$@" ;;
    enter) cmd_enter ;;
    key) cmd_key "$@" ;;
    keys) cmd_keys "$@" ;;
    key-down) send key "$(keycode "$1")" 1 ;;
    key-up) send key "$(keycode "$1")" 0; sleep 0.2 ;;
    move) cmd_move "$@" ;;
    mouse-to) cmd_mouse_to "$@" ;;
    click) cmd_click "$@" ;;
    drag-to) cmd_drag_to "$@" ;;
    hold) cmd_hold "$@" ;;
    hold-start) send btn "${1:-left}" 1 ;;
    hold-end) send btn "${1:-left}" 0; sleep 0.2 ;;
    scroll) cmd_scroll "$@" ;;
    resize) cmd_resize "$@" ;;
    shot) cmd_shot "$@" ;;
    title) cmd_title ;;
    log) cmd_log ;;
    stop) cmd_stop ;;
    *) sed -n '2,/^set -euo/p' "$0" | sed '$d; s/^# \{0,1\}//'; exit 1 ;;
esac
