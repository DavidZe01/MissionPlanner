/*
 * No-op stand-ins for the few user32.dll functions that Windows-only parts of
 * libraries call while painting (ObjectListView: update regions, list view
 * messages). Mono's Windows Forms implements the controls themselves; without
 * these, each paint throws DllNotFoundException. Every function reports
 * "nothing done", which the callers treat as an unsupported feature.
 */
#include <stdint.h>
#include <string.h>

typedef intptr_t HWND;
typedef intptr_t LRESULT;

#define SENDMESSAGE(name) \
    LRESULT name(HWND hwnd, uint32_t msg, intptr_t wparam, intptr_t lparam) { return 0; }
SENDMESSAGE(SendMessage)
SENDMESSAGE(SendMessageW)
SENDMESSAGE(SendMessageA)

int GetUpdateRect(HWND hwnd, int32_t *rect, int erase) { if (rect) memset(rect, 0, 16); return 0; }
int ValidateRect(HWND hwnd, const int32_t *rect) { return 1; }
int InvalidateRect(HWND hwnd, const int32_t *rect, int erase) { return 1; }
int GetClientRect(HWND hwnd, int32_t *rect) { if (rect) memset(rect, 0, 16); return 0; }
int ShowWindow(HWND hwnd, int cmd) { return 0; }
int SetWindowPos(HWND hwnd, HWND after, int x, int y, int cx, int cy, uint32_t flags) { return 1; }
int GetScrollInfo(HWND hwnd, int bar, void *info) { return 0; }

#define LONGFN(name, type) \
    type name(HWND hwnd, int index) { return 0; } \
    type name##W(HWND hwnd, int index) { return 0; } \
    type name##A(HWND hwnd, int index) { return 0; }
LONGFN(GetWindowLong, int32_t)
LONGFN(GetWindowLongPtr, intptr_t)

#define SETLONGFN(name, type) \
    type name(HWND hwnd, int index, type value) { return 0; } \
    type name##W(HWND hwnd, int index, type value) { return 0; } \
    type name##A(HWND hwnd, int index, type value) { return 0; }
SETLONGFN(SetWindowLong, int32_t)
SETLONGFN(SetWindowLongPtr, intptr_t)
