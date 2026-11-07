# Mission Planner Windows vs. el port de macOS: diferencias y plan

Este documento compara la versión original (Windows, rama `master` de
ArduPilot/MissionPlanner, sin commits nuevos desde la base del port) con
el port de macOS. Para cada función que depende del sistema operativo indica qué hace
Windows, qué hacía el port, qué se corrigió y qué falta, con las alternativas
posibles y la recomendada. `PARITY.md` tiene los resultados medidos en CI.

## 0. La app nativa (sin Mono ni XQuartz)

Además del port X11 (secciones 1 y 2), ahora hay una **app nativa de macOS**
(`macOS/native/`, workflow `macos-native.yml`). Compila el mismo código de
Mission Planner para .NET 10 de macOS (CoreCLR arm64, dentro del `.app`) con el
Windows Forms de eM Client que dibuja con Cocoa (`ExtLibs/mac-playground`, MIT) y
System.Drawing sobre CoreGraphics:

- Ventanas `NSWindow`/`NSView` reales, nitidez Retina, barra de menús de macOS
  (Cmd+C/V/X/A/Z, Cmd+Q, Ocultar, Minimizar), icono en el Dock.
- No hace falta instalar nada: van dentro del `.app` SDL2/SDL3 (joystick), libusb (DFU),
  SimpleBLE (Bluetooth), los simuladores SITL y **GStreamer** con los plugins que usa Mission
  Planner (RTP/UDP/TCP/RTSP, H.264/H.265 decodificado por VideoToolbox, cámaras AVFoundation,
  MJPEG, MP4; 20 MB). En CI se desinstala el GStreamer de Homebrew antes de las pruebas.
- **OpenGL nativo**: el HUD y las vistas 3D dibujan con un `NSOpenGLContext` sobre la vista,
  como en Windows con WGL (GDI+ sigue disponible como opción del HUD).
- Abrir un `.tlog`, `.bin` o `.log` desde Finder (o arrastrarlo al Dock) lo abre en
  Mission Planner, como las asociaciones de archivos de Windows.
- Los permisos de Bluetooth, cámara y red local se piden a nombre de Mission Planner.

Con esto quedan resueltos los puntos 2.1 (interfaz X11), 2.2 (OpenGL), 2.3 (GStreamer),
2.5 (abrir archivos desde Finder) y 2.7 (Mono y XQuartz como requisitos). El punto 2.8
(firma Developer ID y notarización) está preparado en el workflow (`sign-notarize.sh`) y
se activa al añadir al repositorio los secretos de una cuenta del Apple Developer Program;
sin ella la app va firmada ad hoc. Decisión (uso personal en tu M4): seguir sin cuenta; la app
se instala con `install.sh`, sin cuarentena, y abre desde Finder sin pasos extra. Siguen pendientes 2.4 (actualizador) y 2.6 (Log Analyzer).

Correcciones que necesitó la biblioteca Cocoa para Mission Planner (detalle en
`ExtLibs/mac-playground/README.md`): identidades de ensamblado compatibles con .NET
Framework, barras de herramientas y menú principal dibujados, botones con tema
(`MyButton`) pintados por su código, diálogos cerrados desde temporizadores,
`Graphics.FromImage` independientes (el HUD salía invertido), `Graphics.Clear` como en
GDI+, `LockBits` de solo lectura sin alterar el bitmap, y `Visible`/`Invalidate` desde
otros hilos. OpenTK se usa sin su capa Cocoa (que descarta los eventos pendientes de la
aplicación): solo carga las funciones OpenGL del contexto de AppKit.

Resultado en CI (Mac Apple Silicon de GitHub, macOS 26.6, ejecución 41847250419):

| Prueba | Resultado |
| --- | --- |
| Arranque nativo arm64 (sin Mono ni XQuartz) | Sigue en marcha a los 90 s; ventana, menú y Dock de macOS; HUD por OpenGL |
| Recorrido de pantallas sin vehículo | 147/148 (99,3 %): las 21 pantallas y las 14 pestañas de Flight Data, 79 de 80 páginas (incluidas las dos vistas 3D OpenGL), los 38 formularios, 9/9 funciones (puertos USB/serie, joystick SDL2, voz, cámaras, video en el HUD con el GStreamer incluido, SITL incluido) |
| Conectado al SITL ArduCopter incluido (TCP) | 38/38 (100 %, HUD con GDI+ directo): 1370 parámetros, escritura y lectura de un parámetro, subida y bajada de una misión, listado MAVFTP, logs a bordo, armar y despegar (6,5 m) |
| Sin Homebrew | El GStreamer de Homebrew se desinstala tras compilar: el video usa el de la app |
| Abrir un `.tlog` desde Finder (LaunchServices) | Se abre y se reproduce en Flight Data |
| Instalar con `install.sh` | Instalada sin atributo de cuarentena; abre y queda en marcha |
| Permisos | Bluetooth y red local se piden a nombre de "Mission Planner" |

El único fallo, `ConfigHWCompass2`, falla igual en Windows y Linux cuando no hay vehículo
conectado.

### Confirmado en tu propio Mac (hardware real, no una VM de CI)

El workflow corre como runner autoalojado en tu MacBook (`runs-on: [self-hosted, macOS,
ARM64]`) además de en los runners de GitHub, lo que además evita la cola de espera por
Apple Silicon. Resultado en tu Mac (macOS 27.0.1, Xcode 27.2, ejecución 42390800641):

| Prueba | Resultado |
| --- | --- |
| OpenGL con GPU real (no el renderizador por software de la VM de CI) | `Vendor: Apple`, `Version: 2.1 Metal - 91.7` — Metal de verdad, confirmado por log |
| Recorrido de pantallas sin vehículo | 150/150 (100 %): 12/12 funciones (joystick incluido), 21/21 pantallas, 79/79 páginas, 38/38 formularios. El mapa sale con la imagen satelital (0 de 96 puntos casi blancos), el zoom y la leyenda siguen al panel, y el texto del HUD tiene el tamaño de GDI+ (una H de 100 mide 71,6 px). La brújula (ConfigHWCompass2) lee los parámetros del vehículo al abrirse: se prueba conectado |
| Conectado al SITL incluido | 42/42 (100 %), 18/18 funciones, 2/2 páginas (modos de vuelo y brújula) |
| Bibliotecas nativas incluidas (SDL2, SDL3, libusb, ...) | Cada una carga en un proceso aparte, con firma válida |
| Bibliotecas que carga la app en tiempo de ejecución | 1432 en total, 0 desde Homebrew o `/usr/local` (63 del propio `.app`, 46 del GStreamer incluido) — confirma que no depende de nada instalado aparte |
| Pantalla Retina virtual (2x), HUD por OpenGL y por GDI+ | Ambos modos arrancan y quedan en marcha; `OpenGL view attached: HUD, backing scale 2` confirma que OpenGL se adjuntó a resolución Retina |
| Abrir un `.tlog` desde Finder | OK |
| Instalar con `install.sh` | Sin atributo de cuarentena; la app instalada arranca y queda en marcha. En tu Mac (runner autoalojado) la app no se sube a GitHub: cada build la deja instalada en `~/Applications/Mission Planner.app` y el zip en `~/MissionPlanner-native/` |

Hallazgos reales en tu Mac:
- **Capturas de pantalla**: `screencapture` falla en este runner ("could not create image
  from display") porque el proceso del runner, al correr como servicio en segundo plano, no
  tiene el permiso "Grabación de pantalla" de Ajustes del Sistema. No afecta a la app en sí
  (arranca, dibuja, todo funciona); solo faltan imágenes de las pruebas automáticas. Para
  arreglarlo: Ajustes del Sistema → Privacidad y seguridad → Grabación de pantalla → dale
  permiso al proceso del runner (`Runner.Worker`) o ábrelo una vez desde Terminal para que
  pida el permiso.
- **El joystick cerraba la app** (`feature:joystick-enumeration`). No era un cuelgue ni un
  periférico: `codesign --verify` daba `invalid signature` para `libSDL2-2.0.0.dylib`, y el
  kernel mata (SIGKILL) al proceso que la carga, en milisegundos y desde cualquier hilo.
  `build.sh` copia SDL2, SDL3, libusb y SimpleBLE desde Homebrew y les cambia el nombre de
  instalación con `install_name_tool`, lo que invalida su firma; `codesign --deep` no las
  vuelve a firmar porque `Contents/MonoBundle` no es una ubicación de código anidado (las
  sella como recursos). Lo mismo habría cerrado la app al flashear por DFU (libusb). Ahora
  `build.sh` firma cada biblioteca y verifica una por una, y el workflow carga cada
  biblioteca incluida en un proceso aparte.
- **El mapa se veía casi en blanco.** La biblioteca Cocoa traducía `CompositingMode.SourceOver`
  al modo de mezcla `Overlay` de CoreGraphics (el "Superponer" de Photoshop) en vez de
  `Normal`. El mapa lo fija antes de dibujar los tiles, y sobre su fondo WhiteSmoke ese modo
  da casi blanco para cualquier color (el azul marino del copyright salía lavanda pálido).
  Afectaba también al HUD en modo GDI+. El recorrido comprueba ahora ese modo de mezcla con
  un píxel y dibuja el mapa a una imagen (`map-render.png`).

En la máquina virtual de CI de GitHub (sin aceleración OpenGL) el mismo código usa el
renderizador por software de Apple en vez de la GPU; en Retina, OpenGL dibuja a la resolución
de la pantalla (las llamadas que Mission Planner hace en puntos se escalan) y el HUD GDI+
dibuja directo en la vista, también a resolución Retina.

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
| 14 | Permisos de privacidad (Bluetooth, cámara, micrófono, red local) | n/a | El ejecutable era el `mono` de Homebrew, que no pertenece a ninguna app: macOS podía cerrar el proceso al primer acceso a Bluetooth o cámara | CI lo confirmó: con el `mono` de Homebrew, macOS atribuye Bluetooth y cámara a `mono-sgen`. El aviso diría "mono-sgen" y el permiso se perdería con cada actualización de Mono. Solución: `mono-host`, un ejecutable dentro del `.app` que carga Mono en el mismo proceso. Verificado en CI (ejecución 41778933586): la app arranca a través de él y macOS atribuye ahora Bluetooth y cámara a `org.ardupilot.missionplanner` (Mission Planner) |
| 15 | Cobertura de pruebas | n/a | El recorrido abría 4 de las 14 pestañas de Flight Data | Abre todas (Scripts, Telemetry Logs, DataFlash Logs, Servo, Payload…) |

Verificado en un Mac de CI (macOS 26, arm64; ejecuciones 41777508138 y 41778933586):
- Arranque nativo arm64 a través de `mono-host`, en un X virtual y en XQuartz.
- Firma del `.app` válida.
- Permisos de Bluetooth y cámara a nombre de Mission Planner.
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
1. ~~Ejecución de CI en modo `probe`~~: hecha. `mono-host`, la firma, la detección de placas, el Bluetooth y la cámara quedan verificados.
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

**App nativa (recomendada):**
1. Una vez: `brew install gh && gh auth login` (la CLI de GitHub, con tu cuenta).
2. En Terminal:
   `curl -fsSL https://raw.githubusercontent.com/DavidZe01/MissionPlanner/macos-arm64/macOS/native/install.sh | bash`.
   Descarga la última compilación correcta, la instala en Aplicaciones y la abre. Así no lleva
   el atributo de cuarentena y se abre desde Finder sin pasos extra, aunque la firma sea ad hoc.
3. No hace falta Mono, XQuartz ni Homebrew para usarla (GStreamer va dentro).
4. Los pasos 2 a 5 de la lista siguiente valen igual (Bluetooth, firmware, video, joystick).
   El log queda en `~/Library/Logs/Mission Planner/MissionPlanner.log`.

**App X11 (Mono + XQuartz):**
1. Descarga el `.app` del build (Actions → ejecución → Artifacts → `MissionPlanner-macos-ARM64`), descomprime, quita la cuarentena (`xattr -dr com.apple.quarantine "Mission Planner.app"`) y ábrelo desde Finder.
2. Bluetooth: al abrir, macOS pide permiso de Bluetooth. Acéptalo y abre el desplegable de puertos: los dispositivos BLE aparecen como `BLE_…`.
3. Firmware: conecta la placa y usa Setup → Install Firmware. Con "Load custom firmware" ya no debería preguntar el tipo de placa.
4. Video: con `brew install gstreamer`, en el HUD, clic derecho → Video → Set GStreamer Source.
5. Joystick: Ctrl+J o el botón Joystick.
6. Si algo falla, envíame `~/Library/Logs/MissionPlanner/launcher.log` y `MissionPlanner.log`.
