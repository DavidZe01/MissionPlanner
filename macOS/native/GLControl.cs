// OpenTK's GLControl for the Cocoa Windows Forms backend (ExtLibs/mac-playground).
//
// The OpenTK.GLControl package is built against .NET Framework's Windows Forms and, on
// macOS, drives a Carbon AGL context, which 64-bit macOS does not have. This control has
// the same members without an OpenGL context: Mission Planner then draws the HUD with
// GDI+ (HUD.OpenGLUsable is false) and the OpenGL 3D views report that OpenGL is not
// available, as in the X11 build. An NSOpenGLContext on the control's NSView can be
// added here later.
using System;
using System.ComponentModel;
using System.Windows.Forms;
using OpenTK.Graphics;
using OpenTK.Platform;

namespace OpenTK
{
    public class GLControl : UserControl
    {
        public GLControl() : this(null)
        {
        }

        public GLControl(GraphicsMode mode) : this(mode, 1, 0, GraphicsContextFlags.Default)
        {
        }

        public GLControl(GraphicsMode mode, int major, int minor, GraphicsContextFlags flags)
        {
            // GraphicsMode.Default would initialise OpenTK's platform layer: keep what was given
            GraphicsMode = mode;
            DoubleBuffered = true;
        }

        /// <summary>false: there is no OpenGL context on this platform yet.</summary>
        public static bool ContextAvailable => false;

        [Browsable(false)]
        public GraphicsMode GraphicsMode { get; }

        [Browsable(false)]
        public IGraphicsContext Context => null;

        [Browsable(false)]
        public IWindowInfo WindowInfo => null;

        [Browsable(false)]
        public bool VSync { get; set; }

        [Browsable(false)]
        public bool IsIdle => true;

        public void MakeCurrent()
        {
            throw new NotSupportedException("OpenGL is not available on this display");
        }

        public void SwapBuffers()
        {
        }
    }
}
