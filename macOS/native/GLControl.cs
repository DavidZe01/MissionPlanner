// OpenTK's GLControl for the Cocoa Windows Forms backend (ExtLibs/mac-playground):
// OpenGL on the control's NSView through an NSOpenGLContext.
//
// The OpenTK.GLControl package is built against .NET Framework's Windows Forms and, on
// macOS, drives a Carbon AGL context, which 64-bit macOS does not have. OpenTK's own
// Cocoa layer cannot be used either: setting it up (MacOSFactory) discards every pending
// event of the application, which is where Windows Forms keeps its posted messages.
//
// So OpenTK is set up without a platform (InitToolkit: it only loads the OpenGL entry
// points), the context is AppKit's, registered with OpenTK as an external context
// (GraphicsContext(handle, getAddress, getCurrent)), and the calls that may come from
// other threads (make current, swap) go through CGL, which is thread safe.
// Legacy (2.1) profile, as the HUD and the 3D views use the fixed-function pipeline.
using System;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AppKit;
using CoreGraphics;
using Foundation;
using OpenTK.Graphics;
using OpenTK.Platform;

namespace OpenTK
{
    public class GLControl : UserControl
    {
        const string OpenGLFramework = "/System/Library/Frameworks/OpenGL.framework/OpenGL";

        [DllImport(OpenGLFramework)]
        static extern int CGLSetCurrentContext(IntPtr context);

        [DllImport(OpenGLFramework)]
        static extern IntPtr CGLGetCurrentContext();

        [DllImport(OpenGLFramework)]
        static extern int CGLFlushDrawable(IntPtr context);

        [DllImport(OpenGLFramework)]
        static extern int CGLSetParameter(IntPtr context, int parameter, ref int value);

        [DllImport(OpenGLFramework)]
        static extern int CGLCreateContext(IntPtr pixelFormat, IntPtr share, out IntPtr context);

        [DllImport(OpenGLFramework)]
        static extern int CGLDestroyContext(IntPtr context);

        const int kCGLCPSwapInterval = 222;

        // the context a worker thread made its own (CreateSharedContext): releasing another
        // context on that thread goes back to it, as no context at all would crash the next
        // OpenGL call there (on Windows such a call does nothing)
        [ThreadStatic]
        static IntPtr threadContext;

        static readonly object initLock = new object();
        static bool toolkitReady;
        static IntPtr openGL;
        static bool? contextAvailable;

        NSOpenGLContext nsContext;
        NSOpenGLPixelFormat pixelFormat;
        IntPtr cglPixelFormat;
        IntPtr cglContext;
        GraphicsContext openTKContext;
        CocoaGLContext context;
        readonly ViewInfo windowInfo;
        bool viewAttached;
        bool? initialVSync;

        public GLControl() : this(null)
        {
        }

        public GLControl(GraphicsMode mode) : this(mode, 1, 0, GraphicsContextFlags.Default)
        {
        }

        public GLControl(GraphicsMode mode, int major, int minor, GraphicsContextFlags flags)
        {
            // GraphicsMode.Default would ask OpenTK's platform layer: keep what was given
            GraphicsMode = mode;
            SetStyle(ControlStyles.Opaque, true);
            SetStyle(ControlStyles.UserPaint, true);
            SetStyle(ControlStyles.AllPaintingInWmPaint, true);
            DoubleBuffered = false;
            windowInfo = new ViewInfo(this);
        }

        /// <summary>
        /// OpenTK without a platform layer: describe a platform it has no window system for and
        /// mark the detection done, as Program.UseOpenTKX11OnMacOS does for X11. Called before
        /// anything else uses OpenTK (MacNative.Init).
        /// </summary>
        public static void InitToolkit()
        {
            lock (initLock)
            {
                if (toolkitReady)
                    return;
                toolkitReady = true;
                try
                {
                    var cfg = typeof(Configuration);
                    var flags = BindingFlags.Static | BindingFlags.NonPublic;
                    cfg.GetField("runningOnUnix", flags).SetValue(null, true);
                    cfg.GetField("runningOnLinux", flags).SetValue(null, false);
                    cfg.GetField("runningOnMacOS", flags).SetValue(null, false);
                    cfg.GetField("<RunningOnWindows>k__BackingField", flags).SetValue(null, false);
                    cfg.GetField("<RunningOnSdl2>k__BackingField", flags).SetValue(null, false);
                    cfg.GetField("<RunningOnX11>k__BackingField", flags).SetValue(null, false);
                    cfg.GetField("<RunningOnMono>k__BackingField", flags).SetValue(null, false);
                    cfg.GetField("initialized", flags).SetValue(null, true);
                    Toolkit.Init(new ToolkitOptions { Backend = PlatformBackend.PreferNative });
                }
                catch (Exception ex)
                {
                    Console.WriteLine("OpenTK setup: " + ex);
                }
            }
        }

        static IntPtr RealAddress(string function)
        {
            if (openGL == IntPtr.Zero)
                openGL = NativeLibrary.Load(OpenGLFramework);
            return NativeLibrary.TryGetExport(openGL, function, out var address) ? address : IntPtr.Zero;
        }

        // OpenTK takes every entry point from here
        static IntPtr GetAddress(string function)
        {
            var real = RealAddress(function);
            if (real == IntPtr.Zero)
                return real;
            switch (function)
            {
                case "glViewport":
                    realViewport ??= Marshal.GetDelegateForFunctionPointer<Int4Function>(real);
                    return scaledViewportAddress;
                case "glScissor":
                    realScissor ??= Marshal.GetDelegateForFunctionPointer<Int4Function>(real);
                    return scaledScissorAddress;
                case "glLineWidth":
                    realLineWidth ??= Marshal.GetDelegateForFunctionPointer<FloatFunction>(real);
                    return scaledLineWidthAddress;
                case "glPointSize":
                    realPointSize ??= Marshal.GetDelegateForFunctionPointer<FloatFunction>(real);
                    return scaledPointSizeAddress;
                case "glGetIntegerv":
                    realGetIntegerv ??= Marshal.GetDelegateForFunctionPointer<GetIntegervFunction>(real);
                    return scaledGetIntegervAddress;
                case "glReadPixels":
                    realReadPixels ??= Marshal.GetDelegateForFunctionPointer<ReadPixelsFunction>(real);
                    return scaledReadPixelsAddress;
            }
            return real;
        }

        // Retina: the drawable has the screen's pixels (wantsBestResolutionOpenGLSurface) while
        // Mission Planner gives viewports, scissor boxes, line widths and read-backs in points,
        // the size of its controls. These entry points scale by the backing scale of the
        // context current on the calling thread (1 on a 1x screen, so they change nothing).
        static readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, float> backingScale =
            new System.Collections.Concurrent.ConcurrentDictionary<IntPtr, float>();

        static float CurrentScale => backingScale.TryGetValue(CGLGetCurrentContext(), out var scale) ? scale : 1f;

        static int Scale(int value, float scale) => scale == 1f ? value : (int)Math.Round(value * scale);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void Int4Function(int a, int b, int c, int d);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void FloatFunction(float value);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void GetIntegervFunction(int name, IntPtr data);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void ReadPixelsFunction(int x, int y, int width, int height, int format, int type, IntPtr pixels);

        const int GL_VIEWPORT = 0x0BA2, GL_SCISSOR_BOX = 0x0C10, GL_UNSIGNED_BYTE = 0x1401, GL_RGBA = 0x1908, GL_BGRA = 0x80E1;

        static Int4Function realViewport, realScissor;
        static FloatFunction realLineWidth, realPointSize;
        static GetIntegervFunction realGetIntegerv;
        static ReadPixelsFunction realReadPixels;

        static readonly Int4Function scaledViewport = (x, y, width, height) =>
        {
            var scale = CurrentScale;
            realViewport(Scale(x, scale), Scale(y, scale), Scale(width, scale), Scale(height, scale));
        };

        static readonly Int4Function scaledScissor = (x, y, width, height) =>
        {
            var scale = CurrentScale;
            realScissor(Scale(x, scale), Scale(y, scale), Scale(width, scale), Scale(height, scale));
        };

        static readonly FloatFunction scaledLineWidth = width => realLineWidth(width * CurrentScale);

        static readonly FloatFunction scaledPointSize = size => realPointSize(size * CurrentScale);

        // the viewport and scissor box read back in points, as they were given
        static readonly GetIntegervFunction scaledGetIntegerv = (name, data) =>
        {
            realGetIntegerv(name, data);
            var scale = CurrentScale;
            if (scale != 1f && data != IntPtr.Zero && (name == GL_VIEWPORT || name == GL_SCISSOR_BOX))
                for (int i = 0; i < 4; i++)
                    Marshal.WriteInt32(data, i * 4, (int)Math.Round(Marshal.ReadInt32(data, i * 4) / scale));
        };

        // a screenshot of the size asked for (points): the screen's pixels, sampled down
        static readonly ReadPixelsFunction scaledReadPixels = (x, y, width, height, format, type, pixels) =>
        {
            var scale = CurrentScale;
            if (scale == 1f || pixels == IntPtr.Zero || width <= 0 || height <= 0 || type != GL_UNSIGNED_BYTE ||
                (format != GL_RGBA && format != GL_BGRA))
            {
                realReadPixels(x, y, width, height, format, type, pixels);
                return;
            }
            int sourceWidth = Scale(width, scale), sourceHeight = Scale(height, scale);
            var source = new int[sourceWidth * sourceHeight];
            var handle = GCHandle.Alloc(source, GCHandleType.Pinned);
            try
            {
                realReadPixels(Scale(x, scale), Scale(y, scale), sourceWidth, sourceHeight, format, type, handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
            var result = new int[width * height];
            for (int row = 0; row < height; row++)
            {
                var sourceRow = Math.Min(sourceHeight - 1, (int)(row * scale)) * sourceWidth;
                for (int column = 0; column < width; column++)
                    result[row * width + column] = source[sourceRow + Math.Min(sourceWidth - 1, (int)(column * scale))];
            }
            Marshal.Copy(result, 0, pixels, result.Length);
        };

        static readonly IntPtr scaledViewportAddress = Marshal.GetFunctionPointerForDelegate(scaledViewport);
        static readonly IntPtr scaledScissorAddress = Marshal.GetFunctionPointerForDelegate(scaledScissor);
        static readonly IntPtr scaledLineWidthAddress = Marshal.GetFunctionPointerForDelegate(scaledLineWidth);
        static readonly IntPtr scaledPointSizeAddress = Marshal.GetFunctionPointerForDelegate(scaledPointSize);
        static readonly IntPtr scaledGetIntegervAddress = Marshal.GetFunctionPointerForDelegate(scaledGetIntegerv);
        static readonly IntPtr scaledReadPixelsAddress = Marshal.GetFunctionPointerForDelegate(scaledReadPixels);

        // the backing scale of the attached view; a change (another screen) resizes the viewport
        void UpdateBackingScale(NSView view)
        {
            var scale = (float)view.ConvertSizeToBacking(new CGSize(1, 1)).Width;
            if (scale <= 0 || float.IsNaN(scale))
                scale = 1f;
            if (backingScale.TryGetValue(cglContext, out var previous) && previous == scale)
                return;
            backingScale[cglContext] = scale;
            Console.WriteLine("OpenGL backing scale " + scale + " for " + GetType().Name);
            // the control sets its viewport on resize (HUD.doResize, OpenGLtest2.test_Resize)
            BeginInvoke((Action)(() => OnResize(EventArgs.Empty)));
        }

        // An accelerated renderer first (the Mac's GPU); else any, such as Apple's software
        // renderer on a virtual machine without OpenGL acceleration (CI runners)
        static readonly NSOpenGLPixelFormatAttribute[][] pixelFormats =
        {
            new[]
            {
                NSOpenGLPixelFormatAttribute.DoubleBuffer, NSOpenGLPixelFormatAttribute.Accelerated,
                NSOpenGLPixelFormatAttribute.ColorSize, (NSOpenGLPixelFormatAttribute)24,
                NSOpenGLPixelFormatAttribute.AlphaSize, (NSOpenGLPixelFormatAttribute)8,
                NSOpenGLPixelFormatAttribute.DepthSize, (NSOpenGLPixelFormatAttribute)24,
                NSOpenGLPixelFormatAttribute.StencilSize, (NSOpenGLPixelFormatAttribute)8,
                (NSOpenGLPixelFormatAttribute)0,
            },
            new[]
            {
                NSOpenGLPixelFormatAttribute.DoubleBuffer,
                NSOpenGLPixelFormatAttribute.ColorSize, (NSOpenGLPixelFormatAttribute)24,
                NSOpenGLPixelFormatAttribute.AlphaSize, (NSOpenGLPixelFormatAttribute)8,
                NSOpenGLPixelFormatAttribute.DepthSize, (NSOpenGLPixelFormatAttribute)24,
                NSOpenGLPixelFormatAttribute.StencilSize, (NSOpenGLPixelFormatAttribute)8,
                (NSOpenGLPixelFormatAttribute)0,
            },
            new[]
            {
                NSOpenGLPixelFormatAttribute.DoubleBuffer,
                NSOpenGLPixelFormatAttribute.DepthSize, (NSOpenGLPixelFormatAttribute)16,
                (NSOpenGLPixelFormatAttribute)0,
            },
        };

        static int pixelFormatIndex = -1;

        static NSOpenGLPixelFormat CreatePixelFormat()
        {
            var first = Math.Max(0, pixelFormatIndex);
            for (int i = first; i < pixelFormats.Length; i++)
            {
                try
                {
                    var format = new NSOpenGLPixelFormat(pixelFormats[i]);
                    if (format.Handle != IntPtr.Zero)
                    {
                        if (pixelFormatIndex != i)
                            Console.WriteLine("OpenGL pixel format " + i + (i == 0 ? " (accelerated)" : " (any renderer)"));
                        pixelFormatIndex = i;
                        return format;
                    }
                }
                catch (Exception ex)
                {
                    // initWithAttributes: returned nil
                    Console.WriteLine("OpenGL pixel format " + i + ": " + ex.Message.Split('\n')[0]);
                }
            }
            return null;
        }

        /// <summary>Whether an accelerated OpenGL context can be made (MISSIONPLANNER_NO_OPENGL: no).</summary>
        public static bool ContextAvailable
        {
            get
            {
                if (contextAvailable == null)
                {
                    try
                    {
                        var format = CreatePixelFormat();
                        contextAvailable = format != null;
                        format?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("OpenGL pixel format: " + ex.Message);
                        contextAvailable = false;
                    }
                }
                return contextAvailable.Value && Environment.GetEnvironmentVariable("MISSIONPLANNER_NO_OPENGL") == null;
            }
        }

        public bool HasValidContext => context != null;

        protected override void OnHandleCreated(EventArgs e)
        {
            DestroyContext();
            try
            {
                InitToolkit();
                pixelFormat = CreatePixelFormat() ?? throw new GraphicsContextException("no OpenGL pixel format");
                cglPixelFormat = pixelFormat.CGLPixelFormat.Handle;
                nsContext = new NSOpenGLContext(pixelFormat, null);
                cglContext = nsContext.CGLContext.Handle;
                // until the view is attached: the main screen's scale
                backingScale[cglContext] = (float)(NSScreen.MainScreen?.BackingScaleFactor ?? 1);
                CGLSetCurrentContext(cglContext);
                // OpenTK loads the entry points of the current context
                openTKContext = new GraphicsContext(new ContextHandle(cglContext), GetAddress,
                    () => new ContextHandle(CGLGetCurrentContext()));
                context = new CocoaGLContext(this);
                if (initialVSync.HasValue)
                {
                    context.SwapInterval = initialVSync.Value ? 1 : 0;
                    initialVSync = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("OpenGL context: " + ex);
                DestroyContext();
            }
            base.OnHandleCreated(e);
            // the view is in its window once the control is shown
            if (context != null)
                BeginInvoke((Action)AttachView);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            base.OnHandleDestroyed(e);
            DestroyContext();
        }

        void DestroyContext()
        {
            if (cglContext != IntPtr.Zero && CGLGetCurrentContext() == cglContext)
                CGLSetCurrentContext(IntPtr.Zero);
            openTKContext?.Dispose();
            openTKContext = null;
            if (nsContext != null)
            {
                if (viewAttached)
                    nsContext.ClearDrawable();
                nsContext.Dispose();
                nsContext = null;
            }
            backingScale.TryRemove(cglContext, out _);
            cglContext = IntPtr.Zero;
            viewAttached = false;
            context = null;
            pixelFormat?.Dispose();
            pixelFormat = null;
            cglPixelFormat = IntPtr.Zero;
        }

        /// <summary>
        /// A context sharing this one's objects (textures, buffers), without a drawable, for a
        /// worker thread to make current there: what OpenTK.GameWindow gives the 3D map's tile
        /// loader on other platforms. CGL, so any thread may create it.
        /// </summary>
        public IGraphicsContext CreateSharedContext()
        {
            if (cglContext == IntPtr.Zero || cglPixelFormat == IntPtr.Zero)
                throw new GraphicsContextException("No OpenGL context to share");
            if (CGLCreateContext(cglPixelFormat, cglContext, out var shared) != 0 || shared == IntPtr.Zero)
                throw new GraphicsContextException("CGLCreateContext failed");
            // OpenTK's entry points and GraphicsContext.CurrentContext for this context too
            var previous = CGLGetCurrentContext();
            CGLSetCurrentContext(shared);
            GraphicsContext registered = null;
            try
            {
                registered = new GraphicsContext(new ContextHandle(shared), GetAddress,
                    () => new ContextHandle(CGLGetCurrentContext()));
            }
            finally
            {
                CGLSetCurrentContext(previous);
            }
            return new SharedCglContext(shared, registered, GraphicsMode);
        }

        // a CGL context of a worker thread (CreateSharedContext)
        sealed class SharedCglContext : IGraphicsContext, IGraphicsContextInternal
        {
            IntPtr cgl;
            GraphicsContext registered;

            public SharedCglContext(IntPtr cgl, GraphicsContext registered, GraphicsMode mode)
            {
                this.cgl = cgl;
                this.registered = registered;
                GraphicsMode = mode;
            }

            public void SwapBuffers()
            {
            }

            public void MakeCurrent(IWindowInfo window)
            {
                if (window == null)
                {
                    if (threadContext == cgl)
                        threadContext = IntPtr.Zero;
                    if (cgl != IntPtr.Zero && CGLGetCurrentContext() == cgl)
                        CGLSetCurrentContext(IntPtr.Zero);
                    return;
                }
                threadContext = cgl;
                CGLSetCurrentContext(cgl);
            }

            public bool IsCurrent => cgl != IntPtr.Zero && CGLGetCurrentContext() == cgl;

            public bool IsDisposed => cgl == IntPtr.Zero;

            public int SwapInterval { get; set; }

            public void Update(IWindowInfo window)
            {
            }

            public GraphicsMode GraphicsMode { get; }

            public bool ErrorChecking { get; set; }

            public void LoadAll() => registered?.LoadAll();

            public IGraphicsContext Implementation => this;

            public ContextHandle Context => new ContextHandle(cgl);

            public IntPtr GetAddress(string function) => GLControl.GetAddress(function);

            public IntPtr GetAddress(IntPtr function) => GLControl.GetAddress(Marshal.PtrToStringAnsi(function));

            public void Dispose()
            {
                if (cgl == IntPtr.Zero)
                    return;
                if (threadContext == cgl)
                    threadContext = IntPtr.Zero;
                if (CGLGetCurrentContext() == cgl)
                    CGLSetCurrentContext(IntPtr.Zero);
                registered?.Dispose();
                registered = null;
                CGLDestroyContext(cgl);
                cgl = IntPtr.Zero;
            }
        }

        // The drawable is the control's NSView while that is shown in a window (main thread
        // only); hidden (another screen in front, say), the surface is released, or it would
        // stay on top of what replaces it.
        void AttachView()
        {
            if (viewAttached || nsContext == null || !IsHandleCreated || !Visible || !NSThread.IsMain)
                return;
            var view = ObjCRuntime.Runtime.GetNSObject<NSView>(Handle);
            if (view?.Window == null || view.IsHiddenOrHasHiddenAncestor)
                return;
            view.WantsBestResolutionOpenGLSurface = true;
            nsContext.View = view;
            viewAttached = true;
            paintRequested = true;
            nsContext.Update();
            UpdateBackingScale(view);
            Console.WriteLine("OpenGL view attached: " + GetType().Name + ", backing scale " +
                (backingScale.TryGetValue(cglContext, out var attachedScale) ? attachedScale : 1f));
        }

        void UpdateDrawable()
        {
            if (nsContext == null || !NSThread.IsMain)
                return;
            if (!Visible || !IsHandleCreated)
            {
                if (viewAttached)
                {
                    nsContext.ClearDrawable();
                    viewAttached = false;
                }
                return;
            }
            if (!viewAttached)
                AttachView();
            else
                nsContext.Update();
        }

        const int WM_PAINT = 0x000F;

        // set by Invalidate (OnInvalidated), a resize or a new drawable
        volatile bool paintRequested = true;

        protected override void OnInvalidated(InvalidateEventArgs e)
        {
            paintRequested = true;
            base.OnInvalidated(e);
        }

        protected override void WndProc(ref Message m)
        {
            // Presenting the OpenGL surface makes AppKit draw the view again (drawRect:, which
            // sends WM_PAINT): paint only what Windows Forms asked for, or each frame asks for
            // the next and the main thread never leaves the drawing. The surface keeps showing
            // the last frame meanwhile.
            if (m.Msg == WM_PAINT && viewAttached)
            {
                if (!paintRequested)
                    return;
                paintRequested = false;
            }
            base.WndProc(ref m);
        }

        protected override void OnResize(EventArgs e)
        {
            paintRequested = true;
            // after the native view has its new frame
            if (IsHandleCreated)
                BeginInvoke((Action)UpdateDrawable);
            base.OnResize(e);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            UpdateDrawable();
            base.OnParentChanged(e);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            UpdateDrawable();
            base.OnVisibleChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            AttachView();
            base.OnPaint(e);
        }

        public void MakeCurrent()
        {
            if (context == null)
                throw new GraphicsContextException("No OpenGL context for " + GetType().Name);
            AttachView();
            CGLSetCurrentContext(cglContext);
        }

        public void SwapBuffers()
        {
            if (cglContext == IntPtr.Zero)
                return;
            AttachView();
            if (viewAttached && NSThread.IsMain)
            {
                var view = ObjCRuntime.Runtime.GetNSObject<NSView>(Handle);
                if (view?.Window != null)
                    UpdateBackingScale(view);
            }
            CGLFlushDrawable(cglContext);
        }

        [Browsable(false)]
        public GraphicsMode GraphicsMode { get; }

        [Browsable(false)]
        public IGraphicsContext Context => context;

        [Browsable(false)]
        public IWindowInfo WindowInfo => windowInfo;

        [Browsable(false)]
        public bool VSync
        {
            get => context != null ? context.SwapInterval != 0 : initialVSync ?? true;
            set
            {
                if (context != null)
                    context.SwapInterval = value ? 1 : 0;
                else
                    initialVSync = value;
            }
        }

        [Browsable(false)]
        public bool IsIdle => true;

        [Browsable(false)]
        public float AspectRatio => ClientSize.Width / (float)Math.Max(1, ClientSize.Height);

        // The control's view as OpenTK's window information
        sealed class ViewInfo : IWindowInfo
        {
            readonly GLControl owner;

            public ViewInfo(GLControl owner)
            {
                this.owner = owner;
            }

            public IntPtr Handle => owner.IsHandleCreated ? owner.Handle : IntPtr.Zero;

            public void Dispose()
            {
            }
        }

        // What Mission Planner calls on GLControl.Context, on the CGL context
        sealed class CocoaGLContext : IGraphicsContext, IGraphicsContextInternal
        {
            readonly GLControl owner;
            int swapInterval = 1;

            public CocoaGLContext(GLControl owner)
            {
                this.owner = owner;
            }

            IntPtr Cgl => owner.cglContext;

            public void SwapBuffers() => owner.SwapBuffers();

            public void MakeCurrent(IWindowInfo window)
            {
                if (window == null)
                {
                    if (Cgl != IntPtr.Zero && CGLGetCurrentContext() == Cgl)
                        CGLSetCurrentContext(threadContext);
                    return;
                }
                owner.AttachView();
                CGLSetCurrentContext(Cgl);
            }

            public bool IsCurrent => Cgl != IntPtr.Zero && CGLGetCurrentContext() == Cgl;

            public bool IsDisposed => Cgl == IntPtr.Zero;

            public int SwapInterval
            {
                get => swapInterval;
                set
                {
                    swapInterval = value;
                    if (Cgl != IntPtr.Zero)
                        CGLSetParameter(Cgl, kCGLCPSwapInterval, ref value);
                }
            }

            public void Update(IWindowInfo window) => owner.UpdateDrawable();

            public GraphicsMode GraphicsMode => owner.GraphicsMode;

            public bool ErrorChecking { get; set; }

            public void LoadAll()
            {
                owner.openTKContext?.LoadAll();
            }

            public IGraphicsContext Implementation => this;

            public ContextHandle Context => new ContextHandle(Cgl);

            public IntPtr GetAddress(string function) => GLControl.GetAddress(function);

            public IntPtr GetAddress(IntPtr function) => GLControl.GetAddress(Marshal.PtrToStringAnsi(function));

            public void Dispose()
            {
            }
        }
    }
}
