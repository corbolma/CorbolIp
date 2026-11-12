// Pulsante sulla barra delle applicazioni mentre l'app è aperta.
// Il widget è una finestra "incollata al desktop" che non compare sulla barra: senza questo pulsante l'icona
// fissata sulla barra non mostra mai la lineetta di "in esecuzione". Qui c'è una finestra invisibile, fuori
// dallo schermo, che serve solo ad avere il pulsante: ha lo stesso AppUserModelID del processo (quindi
// dell'icona fissata), un clic porta il widget in primo piano e l'anteprima al passaggio del mouse mostra il
// widget. "Chiudi finestra" dal tasto destro sul pulsante chiude l'app.
// Stesso file in CorbolIp e CorbolSys: cambia solo il namespace.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CorbolIp
{
    class TaskbarButton : Form
    {
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hbmp, uint flags);
        [DllImport("dwmapi.dll")] static extern int DwmInvalidateIconicBitmaps(IntPtr hwnd);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        const int WS_EX_APPWINDOW = 0x40000;
        const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7, DWMWA_HAS_ICONIC_BITMAP = 10, DWMWA_DISALLOW_PEEK = 11;
        const int WM_SYSCOMMAND = 0x112, WM_DWMSENDICONICTHUMBNAIL = 0x323;
        const int SC_MINIMIZE = 0xF020, SC_MAXIMIZE = 0xF030, SC_RESTORE = 0xF120;

        readonly Control widget;
        readonly Action onClick, onClose;
        readonly Timer refresh;
        bool closingFromApp;

        public TaskbarButton(Control widget, string title, Icon icon, Action onClick, Action onClose)
        {
            this.widget = widget;
            this.onClick = onClick;
            this.onClose = onClose;
            Text = title;
            Icon = icon;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = true;
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-20000, -20000);
            Size = new Size(1, 1);
            Opacity = 0;
            // L'anteprima viene chiesta di nuovo solo se la si dichiara superata: ogni pochi secondi basta
            refresh = new Timer { Interval = 3000 };
            refresh.Tick += delegate { try { DwmInvalidateIconicBitmaps(Handle); } catch { } };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= WS_EX_APPWINDOW; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                int on = 1;
                DwmSetWindowAttribute(Handle, DWMWA_FORCE_ICONIC_REPRESENTATION, ref on, 4);
                DwmSetWindowAttribute(Handle, DWMWA_HAS_ICONIC_BITMAP, ref on, 4);
                DwmSetWindowAttribute(Handle, DWMWA_DISALLOW_PEEK, ref on, 4);
            }
            catch { }
            refresh.Start();
        }

        // Clic sul pulsante (o scelta in Alt+Tab): la finestra invisibile si attiva e passa la mano al widget
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            BeginInvoke(onClick);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_SYSCOMMAND)
            {
                int sc = (int)m.WParam & 0xFFF0;
                if (sc == SC_MINIMIZE || sc == SC_MAXIMIZE || sc == SC_RESTORE) { BeginInvoke(onClick); return; }
            }
            if (m.Msg == WM_DWMSENDICONICTHUMBNAIL)
            {
                long lp = m.LParam.ToInt64();
                SendThumbnail((int)((lp >> 16) & 0xFFFF), (int)(lp & 0xFFFF));
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void SendThumbnail(int maxW, int maxH)
        {
            if (maxW <= 0 || maxH <= 0) return;
            Bitmap shot = null;
            try
            {
                if (widget.Visible && widget.Width > 0 && widget.Height > 0)
                {
                    shot = new Bitmap(widget.Width, widget.Height);
                    widget.DrawToBitmap(shot, new Rectangle(Point.Empty, widget.Size));
                }
            }
            catch { if (shot != null) { shot.Dispose(); shot = null; } }

            int srcW = shot != null ? shot.Width : 64, srcH = shot != null ? shot.Height : 64;
            float k = Math.Min(1f, Math.Min((float)maxW / srcW, (float)maxH / srcH));
            int w = Math.Max(1, (int)(srcW * k)), h = Math.Max(1, (int)(srcH * k));
            try
            {
                using (var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        if (shot != null) g.DrawImage(shot, 0, 0, w, h);
                        else using (var ic = new Icon(Icon, w, h)) g.DrawIcon(ic, new Rectangle(0, 0, w, h));   // widget nascosto: l'icona
                    }
                    IntPtr hb = bmp.GetHbitmap(Color.FromArgb(0));
                    try { DwmSetIconicThumbnail(Handle, hb, 0); }
                    finally { DeleteObject(hb); }
                }
            }
            catch { }
            finally { if (shot != null) shot.Dispose(); }
        }

        // L'app si chiude: via anche il pulsante
        public void CloseFromApp()
        {
            closingFromApp = true;
            refresh.Stop();
            Close();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // "Chiudi finestra" sul pulsante della barra: chiude l'app intera (allo spegnimento di Windows si lascia chiudere)
            if (!closingFromApp && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; BeginInvoke(onClose); }
            base.OnFormClosing(e);
        }
    }
}
