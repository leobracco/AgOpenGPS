using OpenTK;

namespace AgOpenGPS
{
    // Punto de aislamiento del host OpenGL (traspaso portabilidad 2026-07-17):
    // el código de render (OpenGL.Designer.cs, Position.designer.cs, etc.)
    // habla con los surfaces solo a través de esta interfaz. En un port se
    // reemplaza la implementación GLControl/WinForms por la surface de la
    // plataforma (EGL/SDL en Linux, GLSurfaceView en Android) y las llamadas
    // GL.* quedan igual.
    public interface IOpenGLSurface
    {
        int Width { get; set; }

        int Height { get; set; }

        int Left { get; set; }

        int Top { get; set; }

        float AspectRatio { get; }

        // Activa el contexto GL de este surface en el hilo actual.
        void MakeCurrent();

        // Presenta el back buffer en pantalla.
        void SwapBuffers();

        // Fuerza un repintado inmediato (dispara el Paint del host).
        void Refresh();
    }

    // Implementación Windows/WinForms sobre OpenTK.GLControl.
    public class WinFormsGlSurface : IOpenGLSurface
    {
        // Control WinForms subyacente: solo para código host (anclaje de
        // widgets, PointToClient). El render no debe usarlo.
        public GLControl Control { get; }

        public WinFormsGlSurface(GLControl control)
        {
            Control = control;
        }

        public int Width
        {
            get { return Control.Width; }
            set { Control.Width = value; }
        }

        public int Height
        {
            get { return Control.Height; }
            set { Control.Height = value; }
        }

        public int Left
        {
            get { return Control.Left; }
            set { Control.Left = value; }
        }

        public int Top
        {
            get { return Control.Top; }
            set { Control.Top = value; }
        }

        public float AspectRatio
        {
            get { return Control.AspectRatio; }
        }

        public void MakeCurrent()
        {
            Control.MakeCurrent();
        }

        public void SwapBuffers()
        {
            Control.SwapBuffers();
        }

        public void Refresh()
        {
            Control.Refresh();
        }
    }
}
