using AgOpenGPS.Core.Models;
using System;
using System.Drawing;
using System.Windows.Forms;

// Helpers WinForms puros (traspaso portabilidad 2026-07-17: movidos de
// Classes/ a Controls/ para que Classes/ quede sin UI; la extensión
// portable CheckColorFor255 vive en Classes/ColorExtensions.cs).
namespace AgOpenGPS
{
    public class NudlessNumericUpDown : NumericUpDown
    {
        public NudlessNumericUpDown()
        {
            Controls[0].Hide();
        }

        protected override void OnTextBoxResize(object source, EventArgs e)
        {
            Controls[1].Width = Width - 4;
        }

        public new decimal Value
        {
            get
            {
                return base.Value;
            }
            set
            {
                if (value != base.Value)
                {
                    if (value < Minimum)
                    {
                        value = Minimum;
                    }
                    if (value > Maximum)
                    {
                        value = Maximum;
                    }
                    base.Value = value;
                }
            }
        }
    }


    /// <summary>
    /// Bitmaps de los modos de tramline. Vivía en CTram.GetModeBitmap; se
    /// movió acá (archivo de helpers UI) para que CTram quede sin
    /// System.Drawing (traspaso portabilidad 2026-07-16).
    /// </summary>
    public static class TramModeBitmaps
    {
        public static Bitmap Get(TramMode mode)
        {
            switch (mode)
            {
                case TramMode.None: return Properties.Resources.TramOff;
                case TramMode.All: return Properties.Resources.TramAll;
                case TramMode.FillTracks: return Properties.Resources.TramLines;
                case TramMode.BoundaryTracks: return Properties.Resources.TramOuter;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mode), "TramMode argument out of range");
            }
        }
    }

    public static class CExtensionMethods
    {
        /// <summary>
        /// Sets the progress bar value, without using 'Windows Aero' animation.
        /// This is to work around a known WinForms issue where the progress bar
        /// is slow to update.
        /// </summary>
        public static void SetProgressNoAnimation(this ProgressBar pb, int value)
        {
            // To get around the progressive animation, we need to move the
            // progress bar backwards.
            if (value == pb.Maximum)
            {
                // Special case as value can't be set greater than Maximum.
                pb.Maximum = value + 1;     // Temporarily Increase Maximum
                pb.Value = value + 1;       // Move past
                pb.Maximum = value;         // Reset maximum
            }
            else
            {
                pb.Value = value + 1;       // Move past
            }
            pb.Value = value;               // Move to correct value
        }
    }
}