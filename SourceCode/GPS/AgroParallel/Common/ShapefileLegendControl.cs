// ============================================================================
// ShapefileLegendControl.cs - Panel de dosis en vivo + control manual
// ============================================================================

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace AgroParallel.Common
{
    public class ShapefileLegendControl : UserControl
    {
        private string _fieldName;
        private double _min, _max, _current;
        private bool _hasCurrent;

        public struct MotorDosis
        {
            public string Nombre;
            public double Objetivo;
            public double Real;
            public bool Activo;
            public bool Manual;
            public double ManualDosis;
        }
        public MotorDosis[] Motores = new MotorDosis[2];
        private bool _hasMotores;
        private bool _connected;
        private bool _buttonsCreated;

        // Selector de nodo (◀ nombre ▶). Cada nodo tiene su propio M0/M1 con
        // dosis independiente; el widget muestra/edita uno por vez.
        public struct NodoInfo { public string Uid; public string Nombre; }
        private List<NodoInfo> _nodos = new List<NodoInfo>();
        private int _nodoIdx = 0;
        public string SelectedNodoUid
        {
            get { return (_nodoIdx >= 0 && _nodoIdx < _nodos.Count) ? _nodos[_nodoIdx].Uid : null; }
        }
        public event Action SelectedNodoChanged;

        // Evento: (motorIdx, manual, dosis). El listener resuelve el nodo
        // activo via SelectedNodoUid.
        public event Action<int, bool, double> MotorManualChanged;

        private Button _btnNodoPrev;
        private Button _btnNodoNext;
        private int _nodoBarY;
        private Button[] _btnMan = new Button[2];
        private Button[] _btnUp = new Button[2];
        private Button[] _btnDn = new Button[2];
        private TextBox[] _txtVal = new TextBox[2];

        private static readonly Color CBg = Color.FromArgb(230, 8, 8, 10);
        private static readonly Color CBorder = Color.FromArgb(40, 40, 44);
        private static readonly Color CText = Color.FromArgb(235, 235, 235);
        private static readonly Color CTextDim = Color.FromArgb(130, 135, 145);
        private static readonly Color CAccent = Color.FromArgb(122, 201, 67);
        private static readonly Color CWarn = Color.FromArgb(255, 220, 0);
        private static readonly Color CError = Color.FromArgb(220, 40, 0);

        public ShapefileLegendControl()
        {
            DoubleBuffered = true;
            Size = new Size(220, 240);
            BackColor = Color.Black;
            SetStyle(ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint, true);
        }

        public void SetLegend(string f, double min, double max) { _fieldName = f; _min = min; _max = max; Invalidate(); }
        public void Clear() { _fieldName = null; _hasCurrent = false; _hasMotores = false; Invalidate(); }
        public void SetCurrent(double v, bool i) { _current = v; _hasCurrent = i; Invalidate(); }
        public void SetConnected(bool c) { _connected = c; Invalidate(); }
        public void SetUdpStatus(bool a, int p, DateTime? l) { _connected = a; Invalidate(); }
        public bool HasData { get { return !string.IsNullOrEmpty(_fieldName) || _hasMotores; } }

        // El caller alimenta la lista de nodos cada tick. Si cambia el set
        // mantengo el UID seleccionado; si desaparece, caigo al primero.
        public void SetNodos(IList<NodoInfo> nodos)
        {
            string prevUid = SelectedNodoUid;
            _nodos.Clear();
            if (nodos != null) _nodos.AddRange(nodos);
            int newIdx = 0;
            if (!string.IsNullOrEmpty(prevUid))
            {
                for (int i = 0; i < _nodos.Count; i++)
                    if (_nodos[i].Uid == prevUid) { newIdx = i; break; }
            }
            if (newIdx != _nodoIdx)
            {
                _nodoIdx = newIdx;
                Invalidate();
            }
        }

        private void CycleNodo(int delta)
        {
            if (_nodos.Count <= 1) return;
            _nodoIdx = (_nodoIdx + delta + _nodos.Count) % _nodos.Count;
            // Reset visual de motores — el siguiente tick los llenará con los
            // valores del nodo nuevo (sin esto se ve un flash con datos viejos).
            for (int i = 0; i < Motores.Length; i++)
            {
                Motores[i].Activo = false;
                Motores[i].Objetivo = 0;
                Motores[i].Real = 0;
            }
            Invalidate();
            var h = SelectedNodoChanged;
            if (h != null) h();
        }

        public void SetMotorDosis(int idx, string nombre, double objetivo, double real, bool activo)
        {
            if (idx < 0 || idx >= 2) return;
            Motores[idx].Nombre = nombre;
            Motores[idx].Objetivo = objetivo;
            Motores[idx].Real = real;
            Motores[idx].Activo = activo;
            _hasMotores = true;
            Invalidate();
        }

        // Sincroniza el estado MAN/AUTO + dosis manual desde la config del nodo
        // seleccionado. Se llama cada tick; evita pisar al usuario si está
        // editando el textbox.
        public void SetMotorManualState(int idx, bool manual, double manualDosis)
        {
            if (idx < 0 || idx >= 2) return;
            bool changed = Motores[idx].Manual != manual
                || Math.Abs(Motores[idx].ManualDosis - manualDosis) > 0.001;
            Motores[idx].Manual = manual;
            Motores[idx].ManualDosis = manualDosis;
            if (!changed) return;
            if (_btnMan[idx] != null)
            {
                _btnMan[idx].Text = manual ? "AUTO" : "MAN";
                _btnMan[idx].ForeColor = manual ? CWarn : CTextDim;
            }
            UpdateManualUI(idx);
        }

        private void CreateButtons()
        {
            if (_buttonsCreated) return;
            _buttonsCreated = true;

            var arrowFont = new Font("Segoe UI", 12f, FontStyle.Bold);
            var manFont = new Font("Segoe UI", 9f, FontStyle.Bold);
            var txtFont = new Font("Segoe UI", 11f, FontStyle.Bold);

            // Barra de selección de nodo: [◀] [Nombre] [▶].
            _btnNodoPrev = new Button
            {
                Text = "\u25C0", FlatStyle = FlatStyle.Flat, Font = arrowFont,
                Size = new Size(36, 28), BackColor = Color.FromArgb(30, 30, 34),
                ForeColor = CText, Cursor = Cursors.Hand, Visible = false
            };
            _btnNodoPrev.FlatAppearance.BorderSize = 0;
            _btnNodoPrev.Click += (s, e) => CycleNodo(-1);
            Controls.Add(_btnNodoPrev);

            _btnNodoNext = new Button
            {
                Text = "\u25B6", FlatStyle = FlatStyle.Flat, Font = arrowFont,
                Size = new Size(36, 28), BackColor = Color.FromArgb(30, 30, 34),
                ForeColor = CText, Cursor = Cursors.Hand, Visible = false
            };
            _btnNodoNext.FlatAppearance.BorderSize = 0;
            _btnNodoNext.Click += (s, e) => CycleNodo(+1);
            Controls.Add(_btnNodoNext);

            for (int mi = 0; mi < 2; mi++)
            {
                int capturedMi = mi;

                _btnMan[mi] = new Button
                {
                    Text = "MAN", FlatStyle = FlatStyle.Flat, Font = manFont,
                    Size = new Size(54, 36), BackColor = Color.FromArgb(30, 30, 34),
                    ForeColor = CTextDim, Cursor = Cursors.Hand, Visible = false
                };
                _btnMan[mi].FlatAppearance.BorderSize = 0;
                _btnMan[mi].Click += (s, e) => ToggleManual(capturedMi);
                Controls.Add(_btnMan[mi]);

                _btnDn[mi] = new Button
                {
                    Text = "\u2212", FlatStyle = FlatStyle.Flat, Font = arrowFont,
                    Size = new Size(40, 36), BackColor = Color.FromArgb(30, 30, 34),
                    ForeColor = CText, Cursor = Cursors.Hand, Visible = false
                };
                _btnDn[mi].FlatAppearance.BorderSize = 0;
                _btnDn[mi].Click += (s, e) => AdjustManual(capturedMi, -1);
                Controls.Add(_btnDn[mi]);

                _txtVal[mi] = new TextBox
                {
                    Text = "0", Font = txtFont,
                    ForeColor = CWarn, BackColor = Color.FromArgb(20, 20, 24),
                    BorderStyle = BorderStyle.FixedSingle,
                    TextAlign = HorizontalAlignment.Center,
                    Size = new Size(56, 32), Visible = false
                };
                _txtVal[mi].KeyDown += (s, e) =>
                {
                    if (e.KeyCode == Keys.Enter)
                    {
                        CommitTypedDose(capturedMi);
                        e.SuppressKeyPress = true;
                    }
                };
                _txtVal[mi].LostFocus += (s, e) => CommitTypedDose(capturedMi);
                Controls.Add(_txtVal[mi]);

                _btnUp[mi] = new Button
                {
                    Text = "+", FlatStyle = FlatStyle.Flat, Font = arrowFont,
                    Size = new Size(40, 36), BackColor = Color.FromArgb(30, 30, 34),
                    ForeColor = CText, Cursor = Cursors.Hand, Visible = false
                };
                _btnUp[mi].FlatAppearance.BorderSize = 0;
                _btnUp[mi].Click += (s, e) => AdjustManual(capturedMi, +1);
                Controls.Add(_btnUp[mi]);
            }
        }

        private void ToggleManual(int mi)
        {
            Motores[mi].Manual = !Motores[mi].Manual;
            if (Motores[mi].Manual && Motores[mi].ManualDosis <= 0)
                Motores[mi].ManualDosis = Motores[mi].Objetivo > 0 ? Motores[mi].Objetivo : 100;

            _btnMan[mi].Text = Motores[mi].Manual ? "AUTO" : "MAN";
            _btnMan[mi].ForeColor = Motores[mi].Manual ? CWarn : CTextDim;
            UpdateManualUI(mi);

            var h = MotorManualChanged;
            if (h != null) h(mi, Motores[mi].Manual, Motores[mi].ManualDosis);
        }

        // direction: -1 (bajar) o +1 (subir). El paso se elige segun el valor
        // actual via AdaptiveStep — 100 g cuando estamos en 5 kg/ha, 1 kg
        // cuando estamos en 200 kg/ha. Asi no es ni demasiado brusco en
        // dosis bajas ni demasiado lento en dosis altas.
        private void AdjustManual(int mi, int direction)
        {
            if (!Motores[mi].Manual) return;
            Motores[mi].ManualDosis = AdaptiveStep.BumpDose(Motores[mi].ManualDosis, direction);
            UpdateManualUI(mi);

            var h = MotorManualChanged;
            if (h != null) h(mi, true, Motores[mi].ManualDosis);
        }

        private void UpdateManualUI(int mi)
        {
            if (_txtVal[mi] == null) return;
            bool m = Motores[mi].Manual;
            // Solo refrescar texto si el campo NO está siendo editado por el
            // usuario (sino le pisamos lo que está tipeando).
            if (m && !_txtVal[mi].Focused)
            {
                // Decimales acordes al paso adaptativo: 5 kg/ha → "5.0",
                // 25 kg/ha → "25.25", 150 kg/ha → "150".
                int dec = AdaptiveStep.DecimalsFor(AdaptiveStep.GetDoseStep(Motores[mi].ManualDosis));
                _txtVal[mi].Text = Motores[mi].ManualDosis.ToString("F" + dec, CultureInfo.InvariantCulture);
            }
            else if (!m)
                _txtVal[mi].Text = "0";
            _txtVal[mi].Visible = m;
            _btnUp[mi].Visible = m;
            _btnDn[mi].Visible = m;
        }

        private void CommitTypedDose(int mi)
        {
            if (_txtVal[mi] == null) return;
            if (!Motores[mi].Manual) return;
            string s = (_txtVal[mi].Text ?? "").Trim().Replace(',', '.');
            double v;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) || v < 0)
            {
                // Texto inválido — restaurar valor previo.
                int decPrev = AdaptiveStep.DecimalsFor(AdaptiveStep.GetDoseStep(Motores[mi].ManualDosis));
                _txtVal[mi].Text = Motores[mi].ManualDosis.ToString("F" + decPrev, CultureInfo.InvariantCulture);
                return;
            }
            Motores[mi].ManualDosis = v;
            int dec = AdaptiveStep.DecimalsFor(AdaptiveStep.GetDoseStep(v));
            _txtVal[mi].Text = v.ToString("F" + dec, CultureInfo.InvariantCulture);
            var h = MotorManualChanged;
            if (h != null) h(mi, true, v);
        }

        // Posiciones de botones por motor (se calculan en OnPaint).
        private int[] _motorBtnY = new int[2];

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using (var path = RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            {
                using (var b = new SolidBrush(CBg)) g.FillPath(b, path);
                using (var p = new Pen(CBorder)) g.DrawPath(p, path);
            }

            int y = 8;

            using (var b = new SolidBrush(_connected ? CAccent : Color.FromArgb(60, 60, 65)))
                g.FillEllipse(b, Width - 16, y, 8, 8);

            // Barra de selección de nodo (visible si hay 1+ nodos).
            if (_hasMotores && _nodos.Count > 0)
            {
                _nodoBarY = y;
                string nodoNombre = (_nodoIdx >= 0 && _nodoIdx < _nodos.Count)
                    ? (_nodos[_nodoIdx].Nombre ?? "Nodo")
                    : "Nodo";
                string label = _nodos.Count > 1
                    ? nodoNombre + "  (" + (_nodoIdx + 1) + "/" + _nodos.Count + ")"
                    : nodoNombre;
                using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
                using (var b = new SolidBrush(CText))
                {
                    var sz = g.MeasureString(label, f);
                    float lx = (Width - sz.Width) / 2f;
                    g.DrawString(label, f, b, lx, y + 6);
                }
                y += 32;
                using (var p = new Pen(CBorder))
                    g.DrawLine(p, 8, y, Width - 8, y);
                y += 4;
            }

            if (_hasMotores)
            {
                for (int mi = 0; mi < 2; mi++)
                {
                    var m = Motores[mi];
                    if (string.IsNullOrEmpty(m.Nombre) && m.Objetivo <= 0 && !m.Activo) continue;

                    Color mColor = mi == 0 ? CAccent : Color.FromArgb(230, 160, 30);

                    using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                    using (var b = new SolidBrush(mColor))
                        g.DrawString(m.Nombre ?? ("M" + mi), f, b, 8, y);

                    if (m.Manual)
                    {
                        using (var f = new Font("Segoe UI", 6.5f, FontStyle.Bold))
                        using (var b = new SolidBrush(CWarn))
                            g.DrawString("MANUAL", f, b, Width - 52, y + 2);
                    }
                    y += 14;

                    using (var f = new Font("Segoe UI", 6.5f))
                    using (var b = new SolidBrush(CTextDim))
                        g.DrawString("OBJ", f, b, 8, y);
                    using (var f = new Font("Segoe UI", 11f, FontStyle.Bold))
                    using (var b = new SolidBrush(CText))
                        g.DrawString(m.Objetivo.ToString("F1", CultureInfo.InvariantCulture), f, b, 32, y - 3);
                    y += 18;

                    int vuW = Width - 18, vuH = 8;
                    var vuRect = new Rectangle(8, y, vuW, vuH);
                    using (var b = new SolidBrush(Color.FromArgb(22, 22, 26)))
                        g.FillRectangle(b, vuRect);

                    double pct = m.Objetivo > 0 ? Math.Min(1.5, m.Real / m.Objetivo) : 0;
                    int fillW = (int)(vuW * Math.Min(1.0, pct));

                    Color fillColor;
                    if (!m.Activo || pct < 0.1) fillColor = Color.FromArgb(50, 50, 55);
                    else if (pct > 1.15) fillColor = CError;
                    else if (pct > 0.85) fillColor = CAccent;
                    else fillColor = CWarn;

                    using (var b = new SolidBrush(fillColor))
                        g.FillRectangle(b, vuRect.X, vuRect.Y, fillW, vuH);
                    using (var p = new Pen(Color.FromArgb(80, 255, 255, 255)))
                        g.DrawLine(p, vuRect.Right, vuRect.Top, vuRect.Right, vuRect.Bottom);
                    using (var p = new Pen(CBorder))
                        g.DrawRectangle(p, vuRect);
                    y += vuH + 2;

                    using (var f = new Font("Segoe UI", 10f, FontStyle.Bold))
                    using (var b = new SolidBrush(fillColor))
                        g.DrawString(m.Real.ToString("F1", CultureInfo.InvariantCulture), f, b, 8, y);
                    string pctStr = m.Objetivo > 0 ? ((int)(pct * 100)) + "%" : "--";
                    using (var f = new Font("Segoe UI", 8f))
                    using (var b = new SolidBrush(CTextDim))
                    {
                        var sz = g.MeasureString(pctStr, f);
                        g.DrawString(pctStr, f, b, Width - sz.Width - 8, y + 2);
                    }
                    y += 18;

                    // Posición para botones MAN de este motor (botones de 36px).
                    _motorBtnY[mi] = y;
                    y += 42;
                }
            }
            else if (HasData)
            {
                using (var f = new Font("Segoe UI", 8f, FontStyle.Bold))
                using (var b = new SolidBrush(CText))
                    g.DrawString(_fieldName ?? "", f, b, 8, y);
                y += 18;
                using (var f = new Font("Segoe UI", 14f, FontStyle.Bold))
                using (var b = new SolidBrush(CText))
                    g.DrawString(_hasCurrent ? _current.ToString("F1", CultureInfo.InvariantCulture) : "--", f, b, 8, y);
                y += 24;
            }

            int newH = y + 4;
            if (Height != newH && newH > 50) Size = new Size(Width, newH);

            if (_hasMotores && !_buttonsCreated) CreateButtons();
            if (_buttonsCreated) LayoutMotorButtons();
        }

        private void LayoutMotorButtons()
        {
            // Selector de nodo (◀ ▶) — uno en cada extremo de la barra.
            if (_btnNodoPrev != null && _btnNodoNext != null)
            {
                bool nodoBarShow = _hasMotores && _nodos.Count > 1;
                _btnNodoPrev.Visible = nodoBarShow;
                _btnNodoNext.Visible = nodoBarShow;
                if (nodoBarShow)
                {
                    _btnNodoPrev.Location = new Point(6, _nodoBarY);
                    _btnNodoNext.Location = new Point(Width - 6 - _btnNodoNext.Width, _nodoBarY);
                }
            }

            // Botones por motor. Layout: [MAN 54] [- 40] [txt 56] [+ 40], gap 4.
            for (int mi = 0; mi < 2; mi++)
            {
                if (_btnMan[mi] == null) continue;
                var m = Motores[mi];
                bool show = !string.IsNullOrEmpty(m.Nombre) || m.Objetivo > 0 || m.Activo;
                _btnMan[mi].Visible = show;
                if (!show) { _btnDn[mi].Visible = false; _txtVal[mi].Visible = false; _btnUp[mi].Visible = false; continue; }

                int by = _motorBtnY[mi];
                int x = 4;
                _btnMan[mi].Location = new Point(x, by); x += _btnMan[mi].Width + 4;
                _btnDn[mi].Location  = new Point(x, by); x += _btnDn[mi].Width + 4;
                _txtVal[mi].Location = new Point(x, by + 2); x += _txtVal[mi].Width + 4;
                _btnUp[mi].Location  = new Point(x, by);

                _btnDn[mi].Visible = m.Manual;
                _txtVal[mi].Visible = m.Manual;
                _btnUp[mi].Visible = m.Manual;
            }
        }

        private static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
