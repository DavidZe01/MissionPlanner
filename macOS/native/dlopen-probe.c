// Isolated repro for the SDL2 dlopen hang seen from JoystickSDL's background
// thread: dlopen the same dylib either from this process's main thread or
// from a pthread, so the two cases run as separate processes (dyld's own
// per-image load lock would otherwise serialize two in-process attempts and
// make a main-thread-only hang look like both are stuck).
// Usage: dlopen-probe <path-to-dylib> main|thread
#include <stdio.h>
#include <dlfcn.h>
#include <pthread.h>
#include <time.h>
#include <unistd.h>

static const char *path;

static double now(void)
{
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return ts.tv_sec + ts.tv_nsec / 1e9;
}

static void probe(void)
{
    double t0 = now();
    printf("dlopen start pid=%d\n", getpid());
    fflush(stdout);
    void *h = dlopen(path, RTLD_NOW);
    if (h)
        printf("dlopen OK handle=%p elapsed=%.3fs\n", h, now() - t0);
    else
        printf("dlopen FAILED err=%s elapsed=%.3fs\n", dlerror(), now() - t0);
    fflush(stdout);
}

static void *worker(void *arg)
{
    (void)arg;
    probe();
    return NULL;
}

int main(int argc, char **argv)
{
    if (argc < 3) {
        fprintf(stderr, "usage: %s <path-to-dylib> main|thread\n", argv[0]);
        return 2;
    }
    path = argv[1];
    if (argv[2][0] == 't') {
        pthread_t t;
        pthread_create(&t, NULL, worker, NULL);
        pthread_join(t, NULL);
    } else {
        probe();
    }
    return 0;
}
