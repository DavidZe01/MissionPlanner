# Mission Planner Windows vs. el port de macOS: diferencias y plan

Este documento compara la versión original (Windows, rama `master` de
ArduPilot/MissionPlanner, sin commits nuevos desde la base del port) con
el port de macOS. Para cada función que depende del sistema operativo indica qué hace
Windows, qué hacía el port, qué se corrigió y qué falta, con las alternativas
posibles y la recomendada. `PARITY.md` tiene los resultados medidos en CI.

## Cómo se hizo la comparación

El port ejecuta el mismo `MissionPlanner.exe` (código C# idéntico salvo los
cambios del port). Por eso las diferencias solo pueden venir de cinco sitios, y se
revisaron todos en las 49 bibliotecas que forman la app (2.803 archivos C#):

1. **Bifurcaciones por plataforma**: cada `MONO`, `Program.MONO`,
   `PlatformID`, `IsOSPlatform`, `Settings.isUnix` (unas 60 relevantes).
2. **Llamadas nativas de Windows** (`DllImport` a user32, gdi32, kernel32,
   setupapi, avifil32, Wlanapi, STDFU, DirectShow…).
3. **APIs de Windows en código gestionado**: registro, WMI, DirectShow,
   DirectInput, SAPI, Dokan, WinUSB, WebBrowser.
4. **Programas externos** que la app ejecuta (`adb.exe`, `runner.exe`,
   `explorer.exe`, `DPInstx64.exe`, `Updater.exe`, SITL `.exe`).
5. **Integración con el escritorio**: instalador, asociaciones de archivos,
   miniaturas de Explorer, actualizaciones, permisos.

Además se repasaron todos los menús, las 14 pestañas de Flight Data, las
páginas de Setup/Config, las ~70 herramientas de Ctrl+F y los formularios.

## 1. Diferencias corregidas en esta ronda

| # | Función | Windows | Port antes | Ahora |
| --- | --- | --- | --- | --- |
| 1 | Detección de placa al instalar firmware (Install Firmware Legacy y "Load custom firmware") | Identifica la placa por VID/PID y nombre USB | Saltaba esa detección en Mono y preguntaba "¿Es una APM 2+? ¿Pixhawk?…": con placas ChibiOS modernas podía elegir el firmware equivocado | Usa los datos USB de IOKit (macOS) o sysfs (Linux). Probado con 5 casos (Cube Orange en bootloader, Matek H743, fmuv2/3, Pixracer) |
| 2 | Conexiones Bluetooth LE | Escanea y lista los dispositivos BLE en el desplegable de puertos | El escaneo solo se registraba en Windows: en macOS nunca aparecían | Escanea con SimpleBLE (CoreBluetooth) cuando la app se abre desde Finder/Dock. CI: el escaneo funciona y macOS pide el permiso de Bluetooth sin cerrar la app |
| 3 | Página "Onboard OSD" (Config) | Visible si el autopiloto tiene OSD | Oculta siempre en Mono | Visible; abre sin errores en el recorrido de pantallas |
| 4 | Navegador de logs DataFlash: tabla | Tabla virtual | En Mono, bloque fijo de 1.000 filas: doble clic, Ctrl+G y el salto desde la gráfica iban a la fila equivocada pasada la 1.000; la barra ignoraba el filtro; cancelar el filtro creaba una fila por línea de log; reabrir la tabla añadía otra barra | Corregidos los cuatro |
| 5 | Actualizaciones | Descarga e instala la nueva versión | Habría instalado la versión de Windows encima del port y roto la app | En macOS avisa de la nueva versión e indica que hay que instalar un nuevo `.app` |
| 6 | Nombres de dispositivo en el desplegable de puertos | "COM5 CubeOrange" (WMI) | Solo la ruta | "/dev/cu.usbmodem1101 CubeOrange" (IOKit) |
| 7 | Evitar que el equipo se duerma con la app abierta | `SetThreadExecutionState` | No | `caffeinate -i -w <pid>` |
| 8 | Botón "carpeta de caché de mapas" | `explorer.exe` | Fallaba | Abre Finder |
| 9 | Flasheo de Parrot Bebop/Disco | `adb.exe` incluido | Buscaba `adb.exe` | Usa `adb` de Android platform-tools (`brew install android-platform-tools`) |
| 10 | Flasheo DFU (firmware `.bin`/`.hex` a placas en modo DFU, página de firmware seguro) | libusb/STDFU | `libusb-1.0` no estaba en el `.app` | `libusb` incluido y mapeado |
| 11 | GStreamer ausente | Lo descarga automáticamente | En ARM64 no hacía nada y el video simplemente no funcionaba | Explica cómo instalarlo (`brew install gstreamer`). Incluirlo en la app es el punto 2.3 |
| 12 | Log Analyzer | Descarga `runner.exe` | Fallaba al ejecutarlo | Explica la alternativa (navegador de logs, plot.ardupilot.org). Portarlo es el punto 2.6 |
| 13 | Firma del `.app` | n/a | `BUILD-INFO.txt` se escribía después de firmar, lo que invalidaba la firma. macOS la usa para identificar la app al conceder permisos | Se firma al final y se verifica la firma |
| 14 | Permisos de privacidad (Bluetooth, cámara, micrófono, red local) | n/a | El ejecutable era el `mono` de Homebrew, que no pertenece a ninguna app: macOS podía cerrar el proceso al primer acceso a Bluetooth o cámara | CI lo confirmó: con el `mono` de Homebrew, macOS atribuye Bluetooth y cámara a `mono-sgen`. El aviso diría "mono-sgen" y el permiso se perdería con cada actualización de Mono. `mono-host` es un ejecutable dentro del `.app` que carga Mono en el mismo proceso; probado en Linux (la app arranca). La primera ejecución en Mac no lo usó por un fallo en la comprobación previa del lanzador (rutas relativas), ya corregido; falta verificarlo en una segunda ejecución |
| 15 | Cobertura de pruebas | n/a | El recorrido abría 4 de las 14 pestañas de Flight Data | Abre todas (Scripts, Telemetry Logs, DataFlash Logs, Servo, Payload…) |

Verificado en un Mac de CI (macOS 26, arm64; ejecución 41777508138):
- Arranque nativo arm64.
- Detección de placas: los 5 casos.
- Video GStreamer en el HUD: 31 cuadros.
- MAVLink con el SITL nativo, listado MAVFTP, servidor WebDAV y montaje.
- Escaneo Bluetooth.

## 2. Diferencias que quedan, con alternativas y plan

### 2.1 La interfaz se dibuja con X11 (XQuartz) en vez de ventanas nativas

**Windows:** ventanas Win32 nativas.
**macOS:** Mono no tiene backend de 64 bits para Cocoa, así que WinForms
dibuja con X11 en XQuartz. Consecuencias:

- Aspecto de Linux, no de macOS. En Retina, XQuartz escala la imagen y el texto se ve menos nítido.
- No se pueden arrastrar archivos desde Finder a la app (listas de MAVFTP y DroneCAN; quedan los botones de subir).
- Sin gesto de pellizco (zoom) del trackpad en el mapa; el desplazamiento con dos dedos sí llega como rueda.
- Los atajos usan Ctrl, no Cmd. El portapapeles se sincroniza mediante XQuartz.
- Hay que tener instalado XQuartz.

Alternativas:

| Opción | Qué es | Pros | Contras |
| --- | --- | --- | --- |
| A. XQuartz (actual) | WinForms de Mono con su driver X11 | Funciona hoy, 98,6 % de las pantallas probadas | Lo descrito arriba |
| **B. `XplatUIMine` + ventana Cocoa** (recomendada a largo plazo) | Upstream ya tiene un driver WinForms (`xplatuimine.cs`, ~3.950 líneas, en meee1/mono) que dibuja toda la interfaz con SkiaSharp; lo usan las versiones Android y Xamarin.MacOS. Se aloja ese lienzo en una ventana Cocoa nativa (NSWindow + CALayer) y se le pasan ratón, teclado y gestos | Sin XQuartz, nitidez Retina, arrastrar desde Finder, gestos, aspecto más integrado. Reutiliza código de ArduPilot | Proyecto grande: le faltan 46 funciones (cursores, tamaños de sistema, algunas de ventanas). Además dibuja todas las ventanas en un solo lienzo (escritorio virtual), así que habría que separar cada formulario en su propia NSWindow. El GLControl (OpenGL) necesitaría otra solución. Estimación: varias semanas |
| C. Driver Cocoa nuevo para WinForms de Mono | Escribir `XplatUICocoa` de 64 bits | La integración más nativa | El más caro: el driver X11 tiene unas 6.000 líneas |
| D. Wine/CrossOver | Ejecutar la versión de Windows | Idéntica | En Apple Silicon usa Rosetta (emulación de x86). Descartado por tu requisito |
| E. Reescribir la interfaz (Avalonia/MAUI) | Nueva interfaz | Nativa | Ya no sería el mismo Mission Planner. Descartado |

Plan: mantener A y, como proyecto aparte, prototipar B. Primero la
pantalla Flight Data en una ventana Cocoa con `XplatUIMine`; si va bien,
separar los formularios en ventanas propias.

### 2.2 HUD por OpenGL y vistas 3D

**Windows:** HUD y mapa 3D con OpenGL (WGL).
**macOS:** XQuartz coloca mal las ventanas hijas de OpenGL (el HUD salía
invertido), así que el HUD usa GDI+ (equivale a la opción "GDI+" de Windows)
y las vistas 3D (Ctrl+F → 3D Map, OpenGLtest) están desactivadas.

Alternativas:
1. **Mesa con GLX "xlib" (recomendada):** un `libGL` de Mesa que renderiza por software (llvmpipe) y dibuja con
   llamadas X11 normales. No usa el GLX de XQuartz, así que evita el fallo
   de las ventanas hijas. Se compila en CI y se mapea `libGL` a esa
   biblioteca solo para las vistas 3D. En un M4, llvmpipe va de sobra para el mapa 3D.
2. Renderizar fuera de pantalla (pbuffer/FBO) y copiar la imagen al control. Depende de que el GLX de XQuartz soporte pbuffers.
3. Con la opción B del punto 2.1, usar una vista Metal/OpenGL nativa.

### 2.3 GStreamer (video en el HUD, cámaras, OSD Video) no va incluido

**Windows:** si falta, la app descarga GStreamer en su carpeta de datos.
**macOS:** hay que instalarlo con `brew install gstreamer`; ahora la app lo explica.

Alternativas:
1. **Descarga bajo demanda del GStreamer oficial (recomendada)**: el paquete
   universal `gstreamer-1.0-<versión>-universal.pkg`, descomprimido con
   `pkgutil --expand-full` en la carpeta de datos, sin permisos de
   administrador, como en Windows. `GStreamer.cs` ya busca el framework.
2. Incluirlo en el `.app`: unos 150–300 MB más.
3. Seguir con Homebrew (actual).

### 2.4 Actualizaciones

**Windows:** el actualizador integrado sustituye los archivos.
**macOS:** ahora solo avisa (punto 1.5). Plan:
1. Que el workflow publique cada build del port como GitHub Release (zip y sha256).
2. En macOS, buscar actualizaciones en esas releases.
3. Instalar con un script que espera a que la app se cierre, sustituye el `.app` con `ditto`, quita la cuarentena y la vuelve a abrir.

Cuando upstream publique una versión nueva, hay que reconstruir el port
encima: fusionar `upstream/master` en `macos-arm64` y pedir un build.

### 2.5 Abrir archivos desde Finder (.tlog, .bin, .log, .waypoints, .param)

**Windows:** el instalador asocia `.tlog`, `.bin` y `.log`, y abrirlos lanza Mission Planner con el archivo.
**macOS:** sin asociaciones. Al hacer doble clic, macOS envía el archivo como evento Apple, no como argumento.
Plan:
1. Declarar `CFBundleDocumentTypes` en `Info.plist`.
2. En `mono-host`, recibir el evento "abrir documentos" (con un bucle de eventos corto al arrancar) y pasar las rutas como argumentos. Mission Planner ya reproduce un `.tlog` y abre un `.bin` que recibe como argumento.
3. Para archivos abiertos con la app ya en marcha, reenviarlos al servidor HTTP local de Mission Planner.

### 2.6 Log Analyzer

**Windows:** descarga y ejecuta `runner.exe`, el LogAnalyzer de ArduPilot congelado en un ejecutable de Windows.
**macOS:** no disponible. El LogAnalyzer en Python ya no existe en ArduPilot master.
Alternativas:
1. Descargar el código Python de la última versión de ArduPilot que lo traía y ejecutarlo con `python3` del sistema. Produce el mismo XML que la app ya sabe mostrar.
2. Enlazar a herramientas web (UAV Log Viewer). Es lo que hace ahora.

### 2.7 Mono, XQuartz y Homebrew como requisitos

**Windows:** solo requiere .NET Framework, que ya trae el sistema.
**macOS:** hay que instalar `mono` y XQuartz.
Plan: incluir Mono en el `.app`. `mono-host` ya carga `libmonosgen`, así que se puede copiar `libmonosgen-2.0.dylib` y `lib/mono/4.5` dentro de
`Contents/Resources/mono` y apuntar `MP_LIBMONO` y `mono_set_dirs` a esa copia (unos 120 MB). Así solo quedaría XQuartz, que desaparecería con la opción B del punto 2.1.

### 2.8 Firma y notarización

La app va firmada ad hoc: hay que quitar la cuarentena con `xattr`. Para
que abra con doble clic sin avisos hace falta una firma Developer ID y
notarización de Apple. Requiere una cuenta del Apple Developer Program (99 USD al año) y los
secretos de la cuenta en el repositorio.

### 2.9 Diferencias menores o de comportamiento

| Función | Windows | macOS | Plan |
| --- | --- | --- | --- |
| Zoom del mapa con la rueda | Centra en el cursor y lo mueve (`SetCursorPos`) | Hace zoom en el cursor sin moverlo | Se puede imitar con `XWarpPointer`. Prioridad baja |
| Gestos táctiles en el mapa | WM_GESTURE | No llegan por X11 | Opción B del punto 2.1 |
| Comprobación de WiFi de Parrot | API WLAN | Se omite; decide la conexión | `networksetup -getairportnetwork`. En macOS 14+ leer el SSID requiere permiso de ubicación |
| Velocidades serie no estándar (921600, 1,5 M) | Driver de Windows | termios de Mono | Verificar con un adaptador FTDI; si falla, ioctl `IOSSIOSPEED` en una pequeña biblioteca nativa |
| Numeración de ejes y botones del joystick | DirectInput | SDL2 | Una configuración de joystick hecha en Windows puede no coincidir: reasignar en macOS |
| Miniaturas de `.tlog` en el explorador | Extensión del Explorer | No | Extensión Quick Look (opcional) |
| Drivers USB, limpieza de drivers, WinUSB | Necesarios en Windows | No hacen falta en macOS (CDC nativo) | n/a |
| Tienda de Windows | Variante empaquetada | n/a | n/a |
| Plugins | C# y DLL de Windows | Los C# compilan (Roslyn); las DLL que usan APIs de Windows no funcionan | n/a |
| Fuentes y tamaños | Segoe UI | Fuentes de macOS vía fontconfig | Revisar en capturas que no haya textos cortados |

### 2.10 Pendiente de verificar con hardware en tu M4

En CI no hay hardware. Esta lista se prueba en tu Mac (pasos en el
apartado 4):
- Bluetooth LE: listar y conectar una radio o autopiloto BLE.
- Cámara local en el HUD: necesita GStreamer y el permiso de cámara.
- Joystick o mando: SDL2.
- Flashear una placa por USB (Install Firmware) y en modo DFU (libusb).
- Video RTSP/UDP en el HUD con GStreamer.
- Escribir archivos a través de la unidad MAVFTP montada (WebDAV).
- Una radio de telemetría a 57600 y un adaptador FTDI a 921600.

## 3. Plan por fases

**Fase 1 (en curso):**
1. Ejecución de CI en modo `probe`: confirmar que `mono-host` funciona, que la firma es válida y qué pasa con el Bluetooth y la cámara con y sin `mono-host`.
2. Si el modo `probe` pasa, publicar el `.app` con las 15 correcciones y que lo pruebes con la lista del apartado 4.
3. Un recorrido completo (`[tests=full]`) para las 14 pestañas de Flight Data.

**Fase 2 (semanas):**
1. Incluir Mono en el `.app` (2.7).
2. Descargar GStreamer bajo demanda (2.3).
3. Releases y actualizador (2.4).
4. Asociaciones de archivos (2.5).
5. OpenGL con Mesa xlib (2.2).
6. Portar el Log Analyzer (2.6).

**Fase 3 (proyecto grande):** quitar XQuartz con `XplatUIMine` y ventanas Cocoa (2.1). Con eso también se resuelven arrastrar archivos, gestos, Retina y la dependencia de XQuartz.

## 4. Cómo verificar en tu Mac

1. Descarga el `.app` del build (Actions → ejecución → Artifacts → `MissionPlanner-macos-ARM64`), descomprime, quita la cuarentena (`xattr -dr com.apple.quarantine "Mission Planner.app"`) y ábrelo desde Finder.
2. Bluetooth: al abrir, macOS pide permiso de Bluetooth. Acéptalo y abre el desplegable de puertos: los dispositivos BLE aparecen como `BLE_…`.
3. Firmware: conecta la placa y usa Setup → Install Firmware. Con "Load custom firmware" ya no debería preguntar el tipo de placa.
4. Video: con `brew install gstreamer`, en el HUD, clic derecho → Video → Set GStreamer Source.
5. Joystick: Ctrl+J o el botón Joystick.
6. Si algo falla, envíame `~/Library/Logs/MissionPlanner/launcher.log` y `MissionPlanner.log`.
