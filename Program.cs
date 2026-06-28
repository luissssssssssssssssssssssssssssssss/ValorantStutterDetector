using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

class Program
{
    private const string SessionName = "DiucaMeterSession";
    private static readonly object lockObj = new object();

    // ── Juego detectado (solo DeadByDaylight) ──
    private static string juegoNombre = "";
    private static int    juegoPID = 0;
    private static bool   juegoAbierto = false;

    // ── FPS con medición precisa ──
    private static int    fpsActuales = 0;
    private static double fpsPromedio = 0;
    private static DateTime ultimoFrameTime = DateTime.Now;
    private static int    framesEnSegundo = 0;
    private static DateTime inicioSegundo = DateTime.Now;
    private static List<double> tiemposEntreFrames = new List<double>();
    private const int MAX_FPS_REAL = 120; // Dead by Daylight cap

    // ── Datos de CPU y RAM ──
    private static double cpuTotalPorcentaje = 0;
    private static double ramTotalPorcentaje = 0;
    private static long   ramTotalMB = 0;
    private static Dictionary<string, double> cpuPorProceso = new Dictionary<string, double>();
    private static Dictionary<string, long> ramPorProceso = new Dictionary<string, long>();

    // ── Para CPU por proceso ──
    private static Dictionary<string, (DateTime time, TimeSpan cpu)> ultimoCpuPorProceso = new Dictionary<string, (DateTime, TimeSpan)>();
    private static int numeroDeNucleos = Environment.ProcessorCount;
    private static DateTime ultimoCalculoCpuTotal = DateTime.Now;

    // ── Procesos activos ──
    private static List<string> procesosTiempoReal = new List<string>();

    // ── Sospechosos de input lag ──
    private static Dictionary<string, Sospechoso> sospechosos = new Dictionary<string, Sospechoso>();
    private static DateTime ultimoEventoInputLag = DateTime.MinValue;
    private const int VENTANA_FPS = 10;
    private const double UMBRAL_FPS_CAIDA = 0.70;

    // ── Registro forense ──
    private static Dictionary<string, RegistroForense> registroForense = new Dictionary<string, RegistroForense>();
    private static Dictionary<string, AcumuladorVentana> ventana = new Dictionary<string, AcumuladorVentana>();
    private static long totalSamplesVentana = 0;
    private static DateTime ultimoResetVentana = DateTime.Now;
    private static Dictionary<string, DateTime> alertaParpadeo = new Dictionary<string, DateTime>();

    // ── Umbrales ──
    private const double UMBRAL_CPU_PCT_CRITICO = 60.0;
    private const double UMBRAL_CPU_PCT_ALTO = 20.0;
    private const long UMBRAL_RAM_MB_ALTO = 200;
    private const long UMBRAL_DISCO_KB = 5000;
    private const long PESO_CPU_SAMPLE = 1;
    private const long PESO_DISCO_MIN = 10;

    // ─── P/Invoke RAM ─────────────────────────────────────────────────────────
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    class Sospechoso
    {
        public string Proceso { get; set; }
        public int VecesDetectado { get; set; }
        public double MaxCPU_Pct { get; set; }
        public long MaxRAM_MB { get; set; }
        public long MaxDisco_KB { get; set; }
        public string TipoDominante { get; set; } = "CPU";
        public DateTime UltimaVez { get; set; }
        public double Peso => (MaxCPU_Pct * 10) + (MaxRAM_MB * 0.5) + (MaxDisco_KB * 0.01) * VecesDetectado;
    }

    class RegistroForense
    {
        public double PicoCPU_Pct = 0;
        public long PicoRAM_MB = 0;
        public long PicoDisco_KB = 0;
        public string HoraPico = "";
        public long DuracionSpike_Ms = 0;
        public int VecesEnUmbral = 0;
        public string TipoDominante = "CPU";
        public bool EsVictima = false;
        public double PicoTotal => (PicoCPU_Pct * 10) + (PicoRAM_MB * 0.5) + (PicoDisco_KB * 0.01);
    }

    class AcumuladorVentana
    {
        public long Samples = 0;
        public long Disco_KB = 0;
        public DateTime InicioSpike = DateTime.MinValue;
    }

    static void Main(string[] args)
    {
        if (!IsAdministrator())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ERROR] Requires Administrator privileges.");
            Console.WriteLine("        Restart console as Administrator.");
            Console.ResetColor();
            Console.ReadKey();
            return;
        }

        Console.Title = "DiucaMeter - DBD Input Lag Hunter";
        Console.Clear();
        Console.CursorVisible = false;

        MEMORYSTATUSEX memStatus = new MEMORYSTATUSEX();
        memStatus.dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX));
        if (GlobalMemoryStatusEx(ref memStatus))
            ramTotalMB = (long)(memStatus.ullTotalPhys / (1024 * 1024));
        else
            ramTotalMB = 16384;

        DibujarEstructuraBase();

        Task.Run(() => IniciarEscuchaKernel());
        Task.Run(() => LoopInterfaz());
        Task.Run(() => MonitorearRAM());
        Task.Run(() => MonitorearCPU_TiempoReal());
        Task.Run(() => MonitorearInputLag());
        Task.Run(() => DetectarDBD()); // Hilo específico para DBD

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
    //  DETECCIÓN EXCLUSIVA DE DEAD BY DAYLIGHT
    // ─────────────────────────────────────────────────────────────────────────
    static void DetectarDBD()
    {
        while (true)
        {
            try
            {
                bool encontrado = false;
                var procesos = Process.GetProcesses();
                foreach (var p in procesos)
                {
                    try
                    {
                        // Buscar cualquier proceso que contenga "DeadByDaylight" (insensible a mayúsculas)
                        if (p.ProcessName.IndexOf("DeadByDaylight", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            lock (lockObj)
                            {
                                juegoNombre = p.ProcessName;
                                juegoPID = p.Id;
                                juegoAbierto = true;
                            }
                            encontrado = true;
                            break;
                        }
                    }
                    catch { }
                }

                if (!encontrado)
                {
                    lock (lockObj)
                    {
                        juegoNombre = "";
                        juegoPID = 0;
                        juegoAbierto = false;
                        fpsActuales = 0;
                        fpsPromedio = 0;
                        tiemposEntreFrames.Clear();
                    }
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  MONITORES (RAM, CPU, INPUT LAG) - sin cambios mayores
    // ─────────────────────────────────────────────────────────────────────────
    static void MonitorearRAM()
    {
        var ramAnterior = new Dictionary<string, long>();
        while (true)
        {
            try
            {
                long totalUsadoMB = 0;
                var nuevosValores = new Dictionary<string, long>();
                var todos = Process.GetProcesses();

                foreach (var p in todos)
                {
                    try
                    {
                        string nombre = p.ProcessName;
                        if (EsFiltradoGlobal(nombre)) continue;

                        long ramMB = p.WorkingSet64 / (1024 * 1024);
                        nuevosValores[nombre] = ramMB;
                        totalUsadoMB += ramMB;

                        if (ramAnterior.TryGetValue(nombre, out long anterior))
                        {
                            long delta = ramMB - anterior;
                            if (delta >= 50)
                                RegistrarImpactoRAM(nombre, delta);
                        }
                        ramAnterior[nombre] = ramMB;
                    }
                    catch { }
                }

                lock (lockObj)
                {
                    ramPorProceso = nuevosValores;
                    ramTotalPorcentaje = ramTotalMB > 0 ? (totalUsadoMB * 100.0) / ramTotalMB : 0;
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
            EnsureRegistro(proceso);
            var reg = registroForense[proceso];
            if (deltaMB > reg.PicoRAM_MB)
            {
                reg.PicoRAM_MB = deltaMB;
                reg.HoraPico = DateTime.Now.ToString("HH:mm:ss");
                reg.VecesEnUmbral++;
                if (deltaMB >= UMBRAL_RAM_MB_ALTO)
                {
                    reg.TipoDominante = "RAM";
                    alertaParpadeo[proceso] = DateTime.Now;
                }
            }
        }
    }

    static void MonitorearCPU_TiempoReal()
    {
        while (true)
        {
            try
            {
                var ahora = DateTime.Now;
                var procesos = Process.GetProcesses();
                var cpuTimes = new Dictionary<string, TimeSpan>();

                foreach (var p in procesos)
                {
                    try
                    {
                        string nombre = p.ProcessName;
                        if (EsFiltradoGlobal(nombre) || string.IsNullOrEmpty(nombre)) continue;
                        cpuTimes[nombre] = p.TotalProcessorTime;
                    }
                    catch { }
                }

                var deltaCpu = new Dictionary<string, double>();
                double deltaTotal = 0;
                foreach (var kv in cpuTimes)
                {
                    string nombre = kv.Key;
                    TimeSpan cpuActual = kv.Value;
                    if (ultimoCpuPorProceso.TryGetValue(nombre, out var anterior))
                    {
                        double delta = (cpuActual - anterior.cpu).TotalSeconds;
                        if (delta > 0)
                        {
                            deltaCpu[nombre] = delta;
                            deltaTotal += delta;
                        }
                    }
                    ultimoCpuPorProceso[nombre] = (ahora, cpuActual);
                }

                double cpuTotal = 0;
                double tiempoTranscurrido = (ahora - ultimoCalculoCpuTotal).TotalSeconds;
                if (tiempoTranscurrido > 0 && numeroDeNucleos > 0)
                {
                    cpuTotal = (deltaTotal / tiempoTranscurrido) * 100 / numeroDeNucleos;
                    if (cpuTotal > 100) cpuTotal = 100;
                    if (cpuTotal < 0) cpuTotal = 0;
                }
                ultimoCalculoCpuTotal = ahora;

                var nuevoCpu = new Dictionary<string, double>();
                if (deltaTotal > 0)
                {
                    foreach (var kv in deltaCpu)
                    {
                        double pct = (kv.Value / deltaTotal) * cpuTotal;
                        nuevoCpu[kv.Key] = Math.Min(100, Math.Max(0, pct));
                    }
                }

                lock (lockObj)
                {
                    cpuPorProceso = nuevoCpu;
                    cpuTotalPorcentaje = cpuTotal;
                }
            }
            catch { }
            Thread.Sleep(1000);
        }
    }

    static void MonitorearInputLag()
    {
        while (true)
        {
            Thread.Sleep(500);

            if (!juegoAbierto || juegoPID == 0 || fpsActuales <= 0)
            {
                lock (lockObj)
                {
                    if (sospechosos.Count > 0 && (DateTime.Now - ultimoEventoInputLag).TotalSeconds > 30)
                        sospechosos.Clear();
                }
                continue;
            }

            // Usar fpsActuales para detectar caídas (ya está limitado a 120)
            lock (lockObj)
            {
                // Guardar en historial para promedio
                if (fpsActuales > 0)
                {
                    // Convertir a lista de enteros para mantener compatibilidad
                    if (!historialFPSInt.Contains(fpsActuales))
                        historialFPSInt.Add(fpsActuales);
                    if (historialFPSInt.Count > VENTANA_FPS)
                        historialFPSInt.RemoveAt(0);
                }
            }

            if (historialFPSInt.Count < 5) continue;
            double promedio = historialFPSInt.Average();
            double umbral = promedio * UMBRAL_FPS_CAIDA;

            if (fpsActuales < umbral && fpsActuales > 0)
            {
                lock (lockObj)
                {
                    if ((DateTime.Now - ultimoEventoInputLag).TotalSeconds < 2)
                        return;

                    ultimoEventoInputLag = DateTime.Now;

                    var procesosActivos = new Dictionary<string, (double cpu, long ram)>();
                    foreach (var kv in cpuPorProceso)
                    {
                        string nombre = kv.Key;
                        if (EsFiltradoGlobal(nombre)) continue;
                        double cpu = kv.Value;
                        long ram = ramPorProceso.TryGetValue(nombre, out long r) ? r : 0;
                        if (cpu > 5.0 || ram > 50)
                            procesosActivos[nombre] = (cpu, ram);
                    }

                    var ordenados = procesosActivos
                        .OrderByDescending(kv => kv.Value.cpu * 2 + kv.Value.ram * 0.1)
                        .Take(5)
                        .ToList();

                    foreach (var kv in ordenados)
                    {
                        string proc = kv.Key;
                        double cpu = kv.Value.cpu;
                        long ram = kv.Value.ram;

                        if (!sospechosos.ContainsKey(proc))
                            sospechosos[proc] = new Sospechoso { Proceso = proc };

                        var s = sospechosos[proc];
                        s.VecesDetectado++;
                        if (cpu > s.MaxCPU_Pct) s.MaxCPU_Pct = cpu;
                        if (ram > s.MaxRAM_MB) s.MaxRAM_MB = ram;
                        if (cpu > 20 && ram < 100)
                            s.TipoDominante = "CPU";
                        else if (ram > 200 && cpu < 20)
                            s.TipoDominante = "RAM";
                        else if (cpu >= 20 && ram >= 100)
                            s.TipoDominante = "CPU+RAM";
                        s.UltimaVez = DateTime.Now;
                    }

                    var aBorrar = sospechosos.Where(kv => (DateTime.Now - kv.Value.UltimaVez).TotalSeconds > 60).Select(kv => kv.Key).ToList();
                    foreach (var key in aBorrar)
                        sospechosos.Remove(key);
                }
            }
        }
    }
    private static List<int> historialFPSInt = new List<int>(); // para promedio

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
        Console.WriteLine(" GAME:                                                 │ FPS: ");
        Console.WriteLine("-----------------------------------------------------------------------------------------");
        Console.SetCursorPosition(0, 10);
        Console.WriteLine(" [ ACTIVE PROCESSES ]                       │  [ HIGH CONSUMPTION ]                    ");
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
        Console.WriteLine(" [ INPUT LAG / STUTTERING DETECTOR ]");
        Console.ResetColor();
        Console.SetCursorPosition(0, 24);
        Console.WriteLine("-----------------------------------------------------------------------------------------");

        Console.SetCursorPosition(0, 28);
        Console.WriteLine("-----------------------------------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(" Press [CTRL + C] to exit safely.");
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
                // ── Cabecera ──
                Console.SetCursorPosition(8, 8);
                if (juegoAbierto)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    string nombre = juegoNombre.Length > 22 ? juegoNombre.Substring(0, 22) : juegoNombre;
                    Console.Write($"{nombre,-22} [PID:{juegoPID,5}]  ");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("NO GAME DETECTED".PadRight(32));
                }
                Console.ResetColor();

                Console.SetCursorPosition(42, 8);
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"CPU: {cpuTotalPorcentaje,5:F1}%  ");
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"RAM: {ramTotalPorcentaje,5:F1}%  ");
                Console.ResetColor();

                Console.SetCursorPosition(70, 8);
                if (juegoAbierto && fpsActuales > 0)
                {
                    Console.ForegroundColor = fpsActuales >= 60 ? ConsoleColor.Green :
                                              fpsActuales >= 30 ? ConsoleColor.Yellow : ConsoleColor.Red;
                    Console.Write($"{fpsActuales,3} FPS");
                    if (fpsPromedio > 0 && Math.Abs(fpsPromedio - fpsActuales) > 1)
                        Console.Write($" (avg {fpsPromedio:F0})");
                    Console.Write("   ");
                }
                else if (juegoAbierto)
                {
                    Console.ForegroundColor = ConsoleColor.DarkYellow;
                    Console.Write("measuring...   ");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("--- FPS        ");
                }
                Console.ResetColor();

                // ── Columna izquierda: ACTIVE PROCESSES ──
                for (int i = 0; i < 8; i++)
                {
                    Console.SetCursorPosition(1, 12 + i);
                    if (i < procesosTiempoReal.Count)
                    {
                        string nombre = procesosTiempoReal[i];
                        bool enAlerta = alertaParpadeo.ContainsKey(nombre) &&
                                        (DateTime.Now - alertaParpadeo[nombre]).TotalMilliseconds < 2000;

                        double cpu = cpuPorProceso.TryGetValue(nombre, out double c) ? c : 0;
                        long ram = ramPorProceso.TryGetValue(nombre, out long r) ? r : 0;

                        string linea = $"{nombre,-14}  {cpu,5:F1}%  {ram,5} MB";
                        if (linea.Length > 42) linea = linea.Substring(0, 42);

                        if (enAlerta && tick % 2 == 0)
                            Console.ForegroundColor = ConsoleColor.Red;
                        else
                            Console.ForegroundColor = ConsoleColor.White;

                        Console.Write(linea.PadRight(42));
                        Console.ResetColor();
                    }
                    else Console.Write(new string(' ', 42));
                }

                // ── Columna derecha: HIGH CONSUMPTION ──
                var topConsumidores = cpuPorProceso
                    .Where(kv => !EsFiltradoGlobal(kv.Key))
                    .Select(kv => new
                    {
                        Proceso = kv.Key,
                        CPU = kv.Value,
                        RAM = ramPorProceso.TryGetValue(kv.Key, out long r) ? r : 0,
                        Peso = kv.Value * 2 + (ramPorProceso.TryGetValue(kv.Key, out long r2) ? r2 * 0.1 : 0)
                    })
                    .OrderByDescending(x => x.Peso)
                    .Take(8)
                    .ToList();

                for (int i = 0; i < 8; i++)
                {
                    Console.SetCursorPosition(47, 12 + i);
                    if (i < topConsumidores.Count)
                    {
                        var item = topConsumidores[i];
                        string nombre = item.Proceso;
                        bool esSospechoso = sospechosos.ContainsKey(nombre);
                        bool enAlerta = alertaParpadeo.ContainsKey(nombre) &&
                                        (DateTime.Now - alertaParpadeo[nombre]).TotalMilliseconds < 2000;

                        string recurso;
                        if (item.CPU > 20 && item.RAM < 100)
                            recurso = $"CPU {item.CPU:F0}%";
                        else if (item.RAM > 200 && item.CPU < 20)
                            recurso = $"RAM {item.RAM}MB";
                        else
                            recurso = $"CPU {item.CPU:F0}% RAM {item.RAM}MB";

                        string icono = esSospechoso ? "⚠ " : "  ";
                        string indicador = esSospechoso ? " [LAG]" : "";

                        Console.ForegroundColor = esSospechoso ? ConsoleColor.Red : ConsoleColor.Yellow;
                        if (enAlerta && tick % 2 == 0)
                            Console.BackgroundColor = ConsoleColor.DarkRed;

                        string linea = $"{icono}{nombre,-12} {recurso,-18}{indicador}";
                        if (linea.Length > 45) linea = linea.Substring(0, 45);
                        Console.Write(linea.PadRight(45));
                        Console.ResetColor();
                    }
                    else
                    {
                        if (i == 0 && topConsumidores.Count == 0)
                        {
                            Console.ForegroundColor = ConsoleColor.DarkGray;
                            Console.Write("No high consumption.".PadRight(45));
                            Console.ResetColor();
                        }
                        else
                            Console.Write(new string(' ', 45));
                    }
                }

                // ── Tercera tabla: INPUT LAG DETECTOR ──
                var topSospechosos = sospechosos
                    .OrderByDescending(kv => kv.Value.Peso)
                    .Take(5)
                    .ToList();

                for (int row = 24; row < 28; row++)
                {
                    Console.SetCursorPosition(1, row);
                    Console.Write(new string(' ', 91));
                }

                Console.SetCursorPosition(1, 24);
                if (topSospechosos.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("PROCESSES DETECTED AS INPUT LAG CAUSE (last 60s):".PadRight(91));
                    Console.ResetColor();

                    for (int i = 0; i < Math.Min(topSospechosos.Count, 3); i++)
                    {
                        var kv = topSospechosos[i];
                        var s = kv.Value;
                        string recurso;
                        if (s.TipoDominante == "CPU")
                            recurso = $"CPU {s.MaxCPU_Pct:F0}%";
                        else if (s.TipoDominante == "RAM")
                            recurso = $"RAM {s.MaxRAM_MB}MB";
                        else
                            recurso = $"CPU {s.MaxCPU_Pct:F0}% + RAM {s.MaxRAM_MB}MB";

                        Console.SetCursorPosition(1, 25 + i);
                        Console.ForegroundColor = ConsoleColor.Yellow;
                        Console.Write($"  {s.Proceso,-16} {recurso,-14} (detected {s.VecesDetectado}x)".PadRight(91));
                        Console.ResetColor();
                    }
                }
                else
                {
                    Console.SetCursorPosition(1, 24);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("No input lag causing processes detected in last 60 seconds.".PadRight(91));
                    Console.ResetColor();
                }
            }

            Thread.Sleep(200);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  KERNEL LISTENER (ETW) - SOLO PARA FPS PRECISO
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
                    KernelTraceEventParser.Keywords.Profile |
                    KernelTraceEventParser.Keywords.DiskFileIO |
                    KernelTraceEventParser.Keywords.FileIO |
                    KernelTraceEventParser.Keywords.Thread
                );

                session.EnableProvider("Microsoft-Windows-DXGI", TraceEventLevel.Informational);

                // ── Manejador de eventos DXGI para FPS ──
                session.Source.Dynamic.All += (TraceEvent data) =>
                {
                    if (data.ProviderName != "Microsoft-Windows-DXGI")
                        return;

                    string eventName = data.EventName;
                    if (string.IsNullOrEmpty(eventName))
                        return;

                    bool isPresent = eventName.Contains("Present", StringComparison.OrdinalIgnoreCase) ||
                                     eventName == "EventID(1)";
                    if (!isPresent)
                        return;

                    int pid = data.ProcessID;
                    lock (lockObj)
                    {
                        if (pid == juegoPID && juegoAbierto)
                        {
                            // Calcular FPS instantáneo basado en tiempo entre frames
                            var ahora = DateTime.Now;
                            double deltaMs = (ahora - ultimoFrameTime).TotalMilliseconds;
                            if (deltaMs > 0 && deltaMs < 1000) // descartar saltos grandes
                            {
                                double fps = 1000.0 / deltaMs;
                                if (fps > MAX_FPS_REAL) fps = MAX_FPS_REAL; // limitar a 120
                                fpsActuales = (int)Math.Round(fps);

                                // Guardar para promedio (últimos 10 frames)
                                tiemposEntreFrames.Add(deltaMs);
                                if (tiemposEntreFrames.Count > 10)
                                    tiemposEntreFrames.RemoveAt(0);
                                if (tiemposEntreFrames.Count > 2)
                                {
                                    double avgMs = tiemposEntreFrames.Average();
                                    double avgFps = 1000.0 / avgMs;
                                    if (avgFps > MAX_FPS_REAL) avgFps = MAX_FPS_REAL;
                                    fpsPromedio = Math.Round(avgFps, 1);
                                }
                            }
                            ultimoFrameTime = ahora;
                        }
                    }
                };

                // ── Manejadores de CPU / disco ──
                session.Source.Kernel.PerfInfoSample += (SampledProfileTraceData data) =>
                {
                    string proc = data.ProcessName;
                    bool filtrado = EsFiltradoGlobal(proc);

                    lock (lockObj)
                    {
                        ResetearVentanaSiCorresponde();
                        totalSamplesVentana += PESO_CPU_SAMPLE;

                        if (!filtrado)
                        {
                            if (!ventana.ContainsKey(proc)) ventana[proc] = new AcumuladorVentana();
                            ventana[proc].Samples += PESO_CPU_SAMPLE;
                            ActualizarProcesoTiempoReal(proc);
                        }
                    }

                    if (!filtrado)
                        EvaluarImpactoCPU(proc);
                };

                session.Source.Kernel.ThreadCSwitch += (CSwitchTraceData data) =>
                {
                    string proc = data.NewProcessName;
                    if (EsFiltradoGlobal(proc)) return;
                    lock (lockObj) { ActualizarProcesoTiempoReal(proc); }
                };

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
    //  EVALUADORES (ETW) - sin cambios
    // ─────────────────────────────────────────────────────────────────────────
    static void EvaluarImpactoCPU(string proceso)
    {
        lock (lockObj)
        {
            ResetearVentanaSiCorresponde();

            if (!ventana.ContainsKey(proceso)) ventana[proceso] = new AcumuladorVentana();
            long samplesProc = ventana[proceso].Samples;
            double pct = totalSamplesVentana > 0 ? (samplesProc * 100.0) / totalSamplesVentana : 0;

            EnsureRegistro(proceso);
            var reg = registroForense[proceso];

            if (pct >= UMBRAL_CPU_PCT_ALTO && ventana[proceso].InicioSpike == DateTime.MinValue)
                ventana[proceso].InicioSpike = DateTime.Now;

            if (pct > reg.PicoCPU_Pct)
            {
                if (ventana[proceso].InicioSpike != DateTime.MinValue)
                    reg.DuracionSpike_Ms = (long)(DateTime.Now - ventana[proceso].InicioSpike).TotalMilliseconds;

                reg.PicoCPU_Pct = pct;
                reg.HoraPico = DateTime.Now.ToString("HH:mm:ss");
                reg.VecesEnUmbral++;

                if (pct >= UMBRAL_CPU_PCT_ALTO && reg.TipoDominante != "RAM")
                    reg.TipoDominante = "CPU";

                if (pct >= UMBRAL_CPU_PCT_CRITICO)
                    alertaParpadeo[proceso] = DateTime.Now;
            }
        }
    }

    static void EvaluarImpactoDisco(string proceso, long kb)
    {
        lock (lockObj)
        {
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
                    alertaParpadeo[proceso] = DateTime.Now;
                }
            }
        }
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
            ultimoResetVentana = DateTime.Now;
        }
    }

    static bool EsFiltradoGlobal(string proceso)
    {
        if (string.IsNullOrEmpty(proceso)) return true;
        if (proceso.Equals("Idle", StringComparison.OrdinalIgnoreCase)) return true;
        if (proceso.Equals("Registry", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}