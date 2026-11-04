/*
 * Contents/MacOS/mono-host: runs Mono inside a process whose executable is in
 * Mission Planner.app, instead of exec'ing Homebrew's mono binary.
 *
 * macOS attributes Bluetooth, camera, microphone and local network access to
 * the app the process belongs to and reads the usage descriptions from its
 * Info.plist. A process whose executable is /opt/homebrew/bin/mono has no app
 * bundle, so macOS would end it on the first Bluetooth or camera access. With
 * this host the executable is inside the bundle; it loads Mono's runtime library
 * and runs Mono's own command-line driver (mono_main), so the arguments are the
 * same as for the mono command:  mono-host [mono options] program.exe [args]
 *
 * MP_LIBMONO names the runtime library (libmonosgen-2.0.1.dylib of the Mono
 * install); the launcher sets it.
 */
#include <dlfcn.h>
#include <stdio.h>
#include <stdlib.h>

int main(int argc, char **argv)
{
    const char *lib = getenv("MP_LIBMONO");
    if (lib == NULL || *lib == '\0') {
        fprintf(stderr, "mono-host: MP_LIBMONO is not set\n");
        return 127;
    }
    void *mono = dlopen(lib, RTLD_NOW | RTLD_GLOBAL);
    if (mono == NULL) {
        fprintf(stderr, "mono-host: %s\n", dlerror());
        return 127;
    }
    int (*mono_main)(int, char **) = (int (*)(int, char **))dlsym(mono, "mono_main");
    if (mono_main == NULL) {
        fprintf(stderr, "mono-host: mono_main not found in %s\n", lib);
        return 127;
    }
    return mono_main(argc, argv);
}
