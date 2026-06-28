using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

class Program
{
    private const string SessionName = "DiucaMeterSession";
    private static readonly object lockObj = new object();

    // ── Juego detectado ──
    private static string juegoNombre   = "";
    private static int    juegoPID      = 0;
    private static bool   juegoAbierto  = false;
    private static int    framesContados = 0;
    private static int    fpsActuales    = 0;
    private static DateTime ultimoCalculoFps = DateTime.Now;

    // Lista de procesos conocidos que son juegos (se amplía fácilmente)
    private static readonly HashSet<string> JUEGOS_CONOCIDOS = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "VALORANT-Win64-Shipping", "RainbowSix", "RainbowSix_BE",
        "FortniteClient-Win64-Shipping", "cs2", "csgo",
        "EscapeFromTarkov", "RustClient", "GTA5", "Cyberpunk2077",
        "witcher3", "eldenring", "sekiro", "darksouls3",
        "bf2042", "bf1", "bfv", "ModernWarfare", "cod",
        "ApexLegends", "r5apex", "Overwatch", "Overwatch2",
        "destiny2", "Warframe", "LeagueOfLegends", "TslGame",
        "ShooterGame", "DeadByDaylight", "Phasmophobia",
        "RocketLeague", "FIFA", "FC24", "FC25",
        "Minecraft", "javaw", "MinecraftLauncher",
        "DyingLightGame", "HorizonZeroDawn", "DEATH_STRANDING",
        "Hades", "hades2", "Returnal", "Starfield",
        "SteamworksExample", "ProjectZomboid"
    };

    // Columna izquierda
    private static List<string> procesosTiempoReal = new List<string>();

    // ── Registro forense PERMANENTE ──
    private static Dictionary<string, RegistroForense> registroForense = new Dictionary<string, RegistroForense>();

    // ── Ventana deslizante de 1 segundo (para picos actuales) ──
    private static Dictionary<string, AcumuladorVentana> ventana     = new Dictionary<string, AcumuladorVentana>();
    private static long totalSamplesVentana = 0; // para calcular % CPU real
    private static DateTime ultimoResetVentana = DateTime.Now;

    // ── Detección de reacción en cadena ──
    // Guardamos timestamp del primer evento crítico de cada proceso
    private static Dictionary<string, DateTime> primerEventoCritico = new Dictionary<string, DateTime>();

    // ── Alertas de parpadeo ──
    private static Dictionary<string, DateTime> alertaParpadeo = new Dictionary<string, DateTime>();

    // ── Umbrales ──
    private const double UMBRAL_CPU_PCT_CRITICO = 60.0;  // % de CPU real
    private const double UMBRAL_CPU_PCT_ALTO    = 20.0;
    private const long   UMBRAL_RAM_MB_ALTO     = 200;
    private const long   UMBRAL_DISCO_KB        = 5000;
    private const long   PESO_CPU_SAMPLE        = 1;    // cada sample = 1 unidad; porcentaje se calcula vs total
    private const long   PESO_CSWITCH           = 0;   // no suma al CPU%, solo detecta actividad
    private const long   PESO_DISCO_MIN         = 10;

    // ── Ventana de correlación para reacción en cadena (ms) ──
    private const int VENTANA_CADENA_MS = 400;

    // ─────────────────────────────────────────────────────────────────────────
    class RegistroForense
    {
        public double PicoCPU_Pct  = 0;    // % CPU real en el peor momento
        public long   PicoRAM_MB   = 0;
        public long   PicoDisco_KB = 0;
        public string HoraPico     = "";
        public long   DuracionSpike_Ms = 0; // cuánto duró el spike
        public int    VecesEnUmbral    = 0; // cuántas veces superó el umbral
        public string TipoDominante    = "CPU";
        public bool   EsVictima        = false; // true = víctima de reacción en cadena

        // Pico total ponderado para ranking
        public double PicoTotal => (PicoCPU_Pct * 10) + (PicoRAM_MB * 0.5) + (PicoDisco_KB * 0.01);
    }

    class AcumuladorVentana
    {
        public long     Samples   = 0;
        public long     Disco_KB  = 0;
        public DateTime InicioSpike = DateTime.MinValue; // cuando empezó a superar umbral
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MAIN
    // ─────────────────────────────────────────────────────────────────────────
    static void Main(string[] args)
    {
        if (!IsAdministrator())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] Requiere privilegios de Administrador.");
            Console.WriteLine("        Reinicia la consola como Administrador.");
            Console.ResetColor();
            Console.ReadKey();
            return;
        }

        Console.Title = "DiucaMeter - Dashboard Forense";
        Console.Clear();
        Console.CursorVisible = false;

        DibujarEstructuraBase();

        Task.Run(() => IniciarEscuchaKernel());
        Task.Run(() => LoopInterfaz());
        Task.Run(() => MonitorearRAM());
        Task.Run(() => DetectarJuego());

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            try { TraceEventSession.GetActiveSession(SessionName)?.Stop(true); } catch { }
            Console.CursorVisible = true;
            Environment.Exit(0);
        };

        while (true) { Thread.Sleep(1000); }
    }

    static bool IsAdministrator()
    {
        var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        var pr = new System.Security.Principal.WindowsPrincipal(id);
        return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  DETECCIÓN DE JUEGO ACTIVO
    //  Escanea procesos cada 2s buscando juegos conocidos O procesos con
    //  GPU activa (heurística: cualquier proceso usando D3D/DXGI)
    // ─────────────────────────────────────────────────────────────────────────
    static void DetectarJuego()
    {
        while (true)
        {
            try
            {
                bool encontrado = false;
                var todos = Process.GetProcesses();

                // Primero buscar en lista de juegos conocidos
                foreach (var p in todos)
                {
                    if (JUEGOS_CONOCIDOS.Contains(p.ProcessName))
                    {
                        lock (lockObj)
                        {
                            juegoNombre  = p.ProcessName;
                            juegoPID     = p.Id;
                            juegoAbierto = true;
                        }
                        encontrado = true;
                        break;
                    }
                }

                // Si no encontramos ninguno conocido, buscar por heurística:
                // proceso con uso de memoria > 500MB y privado > 200MB (típico de juego)
                if (!encontrado)
                {
                    Process candidato = null;
                    long maxMem = 0;
                    foreach (var p in todos)
                    {
                        try
                        {
                            // Excluir procesos de sistema y navegadores comunes
                            if (EsProcesoDeSistema(p.ProcessName)) continue;
                            long mem = p.WorkingSet64;
                            if (mem > 500L * 1024 * 1024 && mem > maxMem)
                            {
                                maxMem    = mem;
                                candidato = p;
                            }
                        }
                        catch { }
                    }

                    if (candidato != null)
                    {
                        lock (lockObj)
                        {
                            juegoNombre  = candidato.ProcessName;
                            juegoPID     = candidato.Id;
                            juegoAbierto = true;
                        }
                        encontrado = true;
                    }
                }

                if (!encontrado)
                {
                    lock (lockObj)
                    {
                        juegoNombre  = "";
                        juegoPID     = 0;
                        juegoAbierto = false;
                        fpsActuales  = 0;
                    }
                }
            }
            catch { }

            Thread.Sleep(2000);
        }
    }

    static bool EsProcesoDeSistema(string nombre)
    {
        var sistema = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon",
            "services", "lsass", "svchost", "dwm", "explorer", "taskmgr",
            "chrome", "firefox", "msedge", "brave", "opera",
            "Code", "devenv", "rider", "idea64",
            "discord", "slack", "teams", "zoom",
            "SearchHost", "StartMenuExperienceHost", "RuntimeBroker",
            "spoolsv", "MsMpEng", "NisSrv", "SecurityHealthService",
            "nvcontainer", "NVDisplay.Container", "nvidia", "aida64"
        };
        return sistema.Contains(nombre);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MONITOR DE RAM
    // ─────────────────────────────────────────────────────────────────────────
    private static Dictionary<string, long> ramAnterior = new Dictionary<string, long>();

    static void MonitorearRAM()
    {
        while (true)
        {
            try
            {
                var todos = Process.GetProcesses();
                foreach (var p in todos)
                {
                    try
                    {
                        string nombre = p.ProcessName;
                        if (EsFiltradoGlobal(nombre)) continue;

                        long ramMB = p.WorkingSet64 / (1024 * 1024);
                        long delta = 0;
                        if (ramAnterior.TryGetValue(nombre, out long anterior))
                            delta = ramMB - anterior;
                        ramAnterior[nombre] = ramMB;

                        if (delta >= 50)
                            RegistrarImpactoRAM(nombre, delta);
                    }
                    catch { }
                }
            }
            catch { }

            Thread.Sleep(500);
        }
    }

    static void RegistrarImpactoRAM(string proceso, long deltaMB)
    {
        lock (lockObj)
        {
            ActualizarProcesoTiempoReal(proceso);
            EnsureRegistro(proceso);
            var reg = registroForense[proceso];

            if (deltaMB > reg.PicoRAM_MB)
            {
                reg.PicoRAM_MB   = deltaMB;
                reg.HoraPico     = DateTime.Now.ToString("HH:mm:ss");
                reg.VecesEnUmbral++;
                if (deltaMB >= UMBRAL_RAM_MB_ALTO)
                {
                    reg.TipoDominante = "RAM";
                    RegistrarEventoCritico(proceso);
                    alertaParpadeo[proceso] = DateTime.Now;
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  UI
    // ─────────────────────────────────────────────────────────────────────────
    static void DibujarEstructuraBase()
    {
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.SetCursorPosition(0, 0);
        Console.WriteLine(@"=========================================================================================");
        Console.WriteLine(@"  ____  _                   __  __      _                                               ");
        Console.WriteLine(@" |  _ \(_)_   _  ___ __ _  |  \/  | ___| |_ ___ _ __                                   ");
        Console.WriteLine(@" | | | | | | | |/ __/ _` | | |\/| |/ _ \ __/ _ \ '__|                                  ");
        Console.WriteLine(@" | |_| | | |_| | (_| (_| | | |  | |  __/ ||  __/ |                                     ");
        Console.WriteLine(@" |____/|_|\__,_|\___\__,_| |_|  |_|\___|\__\___|_|                                     ");
        Console.WriteLine(@"=========================================================================================");
        Console.ResetColor();

        Console.SetCursorPosition(0, 8);
        Console.WriteLine(" JUEGO:                                    │ FPS: ");
        Console.WriteLine("-----------------------------------------------------------------------------------------");
        Console.SetCursorPosition(0, 10);
        Console.WriteLine(" [ PROCESOS ACTIVOS (TIEMPO REAL) ]         │  [ PANEL FORENSE - PICO HISTÓRICO ]       ");
        Console.SetCursorPosition(0, 11);
        Console.WriteLine(" ---------------------------------          │  -----------------------------------------");

        for (int i = 12; i < 22; i++)
        {
            Console.SetCursorPosition(44, i);
            Console.Write("│");
        }

        Console.SetCursorPosition(0, 22);
        Console.WriteLine("-----------------------------------------------------------------------------------------");
        Console.SetCursorPosition(0, 23);
        Console.ForegroundColor = ConsoleColor.DarkCyan;
        Console.WriteLine(" [ ANÁLISIS DE REACCIÓN EN CADENA ]");
        Console.ResetColor();
        Console.SetCursorPosition(0, 24);
        Console.WriteLine("-----------------------------------------------------------------------------------------");

        Console.SetCursorPosition(0, 28);
        Console.WriteLine("-----------------------------------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" Presiona [CTRL + C] para salir de forma segura.");
        Console.ResetColor();
    }

    static void LoopInterfaz()
    {
        int tick = 0;
        while (true)
        {
            tick++;
            lock (lockObj)
            {
                // ── Juego y FPS ──
                Console.SetCursorPosition(8, 8);
                if (juegoAbierto)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    string label = juegoNombre.Length > 30 ? juegoNombre.Substring(0, 30) : juegoNombre;
                    Console.Write($"{label.PadRight(30)}  ");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("SIN JUEGO DETECTADO              ");
                }
                Console.ResetColor();

                Console.SetCursorPosition(48, 8);
                if (juegoAbierto && fpsActuales > 0)
                {
                    Console.ForegroundColor = fpsActuales >= 60 ? ConsoleColor.Green :
                                              fpsActuales >= 30 ? ConsoleColor.Yellow : ConsoleColor.Red;
                    Console.Write($"{fpsActuales} FPS   ");
                }
                else if (juegoAbierto)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write("midiendo...");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("--- FPS    ");
                }
                Console.ResetColor();

                // ── Columna izquierda: procesos activos ──
                for (int i = 0; i < 8; i++)
                {
                    Console.SetCursorPosition(1, 12 + i);
                    if (i < procesosTiempoReal.Count)
                    {
                        string nombre = procesosTiempoReal[i];
                        bool enAlerta = alertaParpadeo.ContainsKey(nombre) &&
                                        (DateTime.Now - alertaParpadeo[nombre]).TotalMilliseconds < 2000;

                        if (enAlerta && tick % 2 == 0)
                            Console.ForegroundColor = ConsoleColor.Red;
                        else
                            Console.ForegroundColor = ConsoleColor.White;

                        string linea = $"• {nombre}".PadRight(42);
                        Console.Write(linea.Substring(0, Math.Min(linea.Length, 42)));
                        Console.ResetColor();
                    }
                    else Console.Write(new string(' ', 42));
                }

                // ── Top 8 forense (columna derecha) ──
                var top8 = registroForense
                    .OrderByDescending(kv => kv.Value.PicoTotal)
                    .Take(8)
                    .ToList();

                // Culpable = #1 que NO es víctima de cadena
                string culpable = "";
                foreach (var kv in top8)
                {
                    if (!kv.Value.EsVictima)
                    {
                        bool esCritico = kv.Value.PicoCPU_Pct >= UMBRAL_CPU_PCT_CRITICO
                                      || kv.Value.PicoRAM_MB  >= UMBRAL_RAM_MB_ALTO;
                        if (esCritico) { culpable = kv.Key; break; }
                    }
                }

                for (int i = 0; i < 8; i++)
                {
                    Console.SetCursorPosition(47, 12 + i);
                    if (i < top8.Count)
                    {
                        var nombre = top8[i].Key;
                        var reg    = top8[i].Value;
                        bool esCulpable = nombre == culpable;
                        bool enAlerta   = alertaParpadeo.ContainsKey(nombre) &&
                                         (DateTime.Now - alertaParpadeo[nombre]).TotalMilliseconds < 2000;

                        // Construir etiqueta de impacto con datos legibles
                        string impacto;
                        if (reg.TipoDominante == "RAM")
                            impacto = $"RAM+{reg.PicoRAM_MB}MB";
                        else if (reg.TipoDominante == "DISCO")
                            impacto = $"DSK {reg.PicoDisco_KB}KB";
                        else
                            impacto = $"CPU {reg.PicoCPU_Pct:F0}%";

                        string duracion = reg.DuracionSpike_Ms > 0 ? $" {reg.DuracionSpike_Ms}ms" : "";
                        string veces    = reg.VecesEnUmbral > 1    ? $" x{reg.VecesEnUmbral}" : "";
                        string victima  = reg.EsVictima             ? " [VICTIMA]" : "";
                        string sufijo   = esCulpable                ? " [CULPABLE]" : victima;

                        string severidad;
                        if (esCulpable && enAlerta && tick % 2 == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.White;
                            Console.BackgroundColor = ConsoleColor.DarkRed;
                            severidad = "!! CRITICO";
                        }
                        else if (esCulpable)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.BackgroundColor = ConsoleColor.Black;
                            severidad = "CRITICO   ";
                        }
                        else if (reg.PicoCPU_Pct >= UMBRAL_CPU_PCT_ALTO || reg.PicoRAM_MB >= 100)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkYellow;
                            Console.BackgroundColor = ConsoleColor.Black;
                            severidad = "ALTO      ";
                        }
                        else
                        {
                            Console.ForegroundColor = ConsoleColor.Gray;
                            Console.BackgroundColor = ConsoleColor.Black;
                            severidad = "LEVE      ";
                        }

                        string linea = $"[{reg.HoraPico}] {severidad} {impacto}{duracion}{veces} {nombre}{sufijo}".PadRight(45);
                        Console.Write(linea.Substring(0, Math.Min(linea.Length, 45)));
                        Console.ResetColor();
                    }
                    else
                    {
                        Console.ResetColor();
                        Console.Write(new string(' ', 45));
                    }
                }

                // ── Panel de reacción en cadena ──
                DibujarPanelCadena(culpable);
            }

            Thread.Sleep(200);
        }
    }

    static void DibujarPanelCadena(string culpable)
    {
        // Encontrar víctimas: procesos que tuvieron su primer evento crítico
        // dentro de VENTANA_CADENA_MS ms después del culpable
        string lineaCadena = "";

        if (!string.IsNullOrEmpty(culpable) && primerEventoCritico.ContainsKey(culpable))
        {
            DateTime tCulpable = primerEventoCritico[culpable];
            var victimas = primerEventoCritico
                .Where(kv => kv.Key != culpable
                          && kv.Value >= tCulpable
                          && (kv.Value - tCulpable).TotalMilliseconds <= VENTANA_CADENA_MS)
                .OrderBy(kv => kv.Value)
                .Select(kv => kv.Key)
                .ToList();

            if (victimas.Count > 0)
            {
                lineaCadena = $" {culpable} → {string.Join(" → ", victimas)} (reacción en cadena detectada)";
                // Marcar víctimas
                foreach (var v in victimas)
                    if (registroForense.ContainsKey(v))
                        registroForense[v].EsVictima = true;
            }
            else
            {
                lineaCadena = $" {culpable} generó el spike. Sin reacción en cadena detectada aún.";
            }
        }
        else if (string.IsNullOrEmpty(culpable))
        {
            lineaCadena = " Monitoreando... Sin spike crítico registrado todavía.";
        }

        for (int row = 25; row < 28; row++)
        {
            Console.SetCursorPosition(1, row);
            Console.Write(new string(' ', 91));
        }

        Console.SetCursorPosition(1, 25);
        if (lineaCadena.Contains("reacción en cadena"))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            string linea = lineaCadena.PadRight(91);
            Console.Write(linea.Substring(0, Math.Min(linea.Length, 91)));
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            string linea = lineaCadena.PadRight(91);
            Console.Write(linea.Substring(0, Math.Min(linea.Length, 91)));
        }
        Console.ResetColor();
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  KERNEL LISTENER
    // ─────────────────────────────────────────────────────────────────────────
    static void IniciarEscuchaKernel()
    {
        TraceEventSession.GetActiveSession(SessionName)?.Stop(true);

        try
        {
            using (var session = new TraceEventSession(SessionName))
            {
                session.StopOnDispose = true;

                session.EnableKernelProvider(
                    KernelTraceEventParser.Keywords.Profile    |
                    KernelTraceEventParser.Keywords.DiskFileIO |
                    KernelTraceEventParser.Keywords.FileIO     |
                    KernelTraceEventParser.Keywords.Thread
                );

                session.EnableProvider("Microsoft-Windows-DXGI");

                // ── CPU Sampling: cuenta samples por proceso y el total ──
                session.Source.Kernel.PerfInfoSample += (SampledProfileTraceData data) =>
                {
                    string proc = data.ProcessName;
                    bool esFiltrado = EsFiltradoGlobal(proc);

                    lock (lockObj)
                    {
                        // Siempre sumar al total de la ventana (incluso los filtrados)
                        // para calcular el % real
                        ResetearVentanaSiCorresponde();
                        totalSamplesVentana += PESO_CPU_SAMPLE;

                        if (!esFiltrado)
                        {
                            if (!ventana.ContainsKey(proc)) ventana[proc] = new AcumuladorVentana();
                            ventana[proc].Samples += PESO_CPU_SAMPLE;
                        }
                    }

                    if (!esFiltrado)
                        EvaluarImpactoCPU(proc);
                };

                // ── Context Switches: solo actualiza lista de tiempo real ──
                session.Source.Kernel.ThreadCSwitch += (CSwitchTraceData data) =>
                {
                    string proc = data.NewProcessName;
                    if (EsFiltradoGlobal(proc)) return;
                    lock (lockObj) { ActualizarProcesoTiempoReal(proc); }
                };

                // ── Disco ──
                session.Source.Kernel.DiskIORead += (DiskIOTraceData data) =>
                {
                    string proc = data.ProcessName;
                    if (EsFiltradoGlobal(proc)) return;
                    long kb = data.TransferSize > 0 ? data.TransferSize / 1024 : PESO_DISCO_MIN;
                    EvaluarImpactoDisco(proc, Math.Max(kb, PESO_DISCO_MIN));
                };

                session.Source.Kernel.DiskIOWrite += (DiskIOTraceData data) =>
                {
                    string proc = data.ProcessName;
                    if (EsFiltradoGlobal(proc)) return;
                    long kb = data.TransferSize > 0 ? data.TransferSize / 1024 : PESO_DISCO_MIN;
                    EvaluarImpactoDisco(proc, Math.Max(kb, PESO_DISCO_MIN));
                };

                // ── DXGI: contar FPS del juego detectado ──
                session.Source.Dynamic.All += (TraceEvent data) =>
                {
                    if (data.ProviderName == "Microsoft-Windows-DXGI"
                     && data.EventName   == "EventID(1)"
                     && data.ProcessID   == juegoPID
                     && juegoAbierto)
                    {
                        lock (lockObj)
                        {
                            framesContados++;
                            if ((DateTime.Now - ultimoCalculoFps).TotalMilliseconds >= 1000)
                            {
                                fpsActuales      = framesContados;
                                framesContados   = 0;
                                ultimoCalculoFps = DateTime.Now;
                            }
                        }
                    }
                };

                session.Source.Process();
            }
        }
        catch (Exception ex)
        {
            lock (lockObj)
            {
                Console.SetCursorPosition(0, 29);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[KERNEL ERROR] {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  EVALUADORES
    // ─────────────────────────────────────────────────────────────────────────

    // Llamar SIN lock (adquiere internamente)
    static void EvaluarImpactoCPU(string proceso)
    {
        lock (lockObj)
        {
            ActualizarProcesoTiempoReal(proceso);
            ResetearVentanaSiCorresponde();

            if (!ventana.ContainsKey(proceso)) ventana[proceso] = new AcumuladorVentana();
            long samplesProc = ventana[proceso].Samples;

            // Calcular % CPU real: samples del proceso / total samples del sistema
            double pct = totalSamplesVentana > 0
                ? (samplesProc * 100.0) / totalSamplesVentana
                : 0;

            EnsureRegistro(proceso);
            var reg = registroForense[proceso];

            // Detectar inicio de spike
            if (pct >= UMBRAL_CPU_PCT_ALTO)
            {
                if (ventana[proceso].InicioSpike == DateTime.MinValue)
                    ventana[proceso].InicioSpike = DateTime.Now;
            }

            if (pct > reg.PicoCPU_Pct)
            {
                // Calcular duración del spike si había uno activo
                if (ventana[proceso].InicioSpike != DateTime.MinValue)
                    reg.DuracionSpike_Ms = (long)(DateTime.Now - ventana[proceso].InicioSpike).TotalMilliseconds;

                reg.PicoCPU_Pct = pct;
                reg.HoraPico    = DateTime.Now.ToString("HH:mm:ss");
                reg.VecesEnUmbral++;

                if (pct >= UMBRAL_CPU_PCT_ALTO && reg.TipoDominante != "RAM")
                    reg.TipoDominante = "CPU";

                if (pct >= UMBRAL_CPU_PCT_CRITICO)
                {
                    RegistrarEventoCritico(proceso);
                    alertaParpadeo[proceso] = DateTime.Now;
                }
            }
        }
    }

    static void EvaluarImpactoDisco(string proceso, long kb)
    {
        lock (lockObj)
        {
            ActualizarProcesoTiempoReal(proceso);
            ResetearVentanaSiCorresponde();

            if (!ventana.ContainsKey(proceso)) ventana[proceso] = new AcumuladorVentana();
            ventana[proceso].Disco_KB += kb;
            long discoActual = ventana[proceso].Disco_KB;

            EnsureRegistro(proceso);
            var reg = registroForense[proceso];

            if (discoActual > reg.PicoDisco_KB)
            {
                reg.PicoDisco_KB = discoActual;
                if (reg.HoraPico == "") reg.HoraPico = DateTime.Now.ToString("HH:mm:ss");

                if (discoActual >= UMBRAL_DISCO_KB && reg.TipoDominante == "CPU" && reg.PicoCPU_Pct < UMBRAL_CPU_PCT_ALTO)
                {
                    reg.TipoDominante = "DISCO";
                    RegistrarEventoCritico(proceso);
                }
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  DETECCIÓN DE REACCIÓN EN CADENA
    // ─────────────────────────────────────────────────────────────────────────

    // Registra el timestamp del PRIMER evento crítico del proceso
    // (sin sobrescribir si ya existía)
    static void RegistrarEventoCritico(string proceso)
    {
        if (!primerEventoCritico.ContainsKey(proceso))
            primerEventoCritico[proceso] = DateTime.Now;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  HELPERS
    // ─────────────────────────────────────────────────────────────────────────

    static void EnsureRegistro(string proceso)
    {
        if (!registroForense.ContainsKey(proceso))
            registroForense[proceso] = new RegistroForense();
    }

    static void ActualizarProcesoTiempoReal(string proceso)
    {
        if (!procesosTiempoReal.Contains(proceso))
        {
            procesosTiempoReal.Insert(0, proceso);
            if (procesosTiempoReal.Count > 8) procesosTiempoReal.RemoveAt(8);
        }
    }

    static void ResetearVentanaSiCorresponde()
    {
        if ((DateTime.Now - ultimoResetVentana).TotalMilliseconds > 1000)
        {
            ventana.Clear();
            totalSamplesVentana = 0;
            ultimoResetVentana  = DateTime.Now;
        }
    }

    static bool EsFiltradoGlobal(string proceso)
    {
        if (string.IsNullOrEmpty(proceso)) return true;
        if (proceso.Equals("Idle",     StringComparison.OrdinalIgnoreCase)) return true;
        if (proceso.Equals("Registry", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}