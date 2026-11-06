// Loads one native library in a process of its own (macos-native.yml, "Load the
// bundled native libraries"): a library with a broken code signature gets the
// whole process killed (SIGKILL) inside dlopen, which inside Mission Planner looks
// like the feature that first loads it crashing. "thread" loads it from a pthread
// instead of the main thread, for libraries that might need the main thread.
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
