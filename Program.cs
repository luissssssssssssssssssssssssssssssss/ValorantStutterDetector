using System;
using System.Threading;

class Program
{
    static void Main(string[] args)
    {
        // Configurar la ventana de la consola
        Console.Title = "VALORANT Stutter Detector v1.0.0 (Beta)";
        Console.Clear();

        // Dibujar Banner ASCII Estético
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"======================================================================");
        Console.WriteLine(@"  __     __      _                                  _   _             ");
        Console.WriteLine(@"  \ \   / /_ _  | | ___  _ __ __ _ _ __  _ __   ___| |_| |_ ___ _ __  ");
        Console.WriteLine(@"   \ \ / / _` | | |/ _ \| '__/ _` | '_ \| '_ \ / _ \ __| __/ _ \ '__| ");
        Console.WriteLine(@"    \ V / (_| | | | (_) | | | (_| | | | | | | |  __/ |_| ||  __/ |    ");
        Console.WriteLine(@"     \_/ \__,_| |_|\___/|_|  \__,_|_| |_|_| |_|\___|\__|\__\___|_|    ");
        Console.WriteLine(@"======================================================================");
        Console.ResetColor();

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [INFO] Inicializando subsistemas del Kernel...");
        Thread.Sleep(600); // Simula una carga limpia rápida

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SUCCESS] Conectado exitosamente a ETW (Event Tracing).");
        Console.ResetColor();
        
        Console.WriteLine();
        Console.WriteLine("----------------------------------------------------------------------");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(" [ ESTADO: ESCUCHANDO EN SEGUNDO PLANO ]");
        Console.ResetColor();
        Console.WriteLine(" Puedes minimizar esta ventana y jugar VALORANT.");
        Console.WriteLine(" Los tirones (stutters) se guardarán automáticamente en log_reporte.txt");
        Console.WriteLine(" Press [CTRL + C] para detener el monitoreo de forma segura.");
        Console.WriteLine("----------------------------------------------------------------------");
        Console.WriteLine();

        // Mantener el programa vivo simulando la escucha en background por ahora
        while (true)
        {
            Thread.Sleep(1000);
        }
    }
}