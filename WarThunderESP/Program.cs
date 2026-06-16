namespace WarThunderESP;

internal static class Program
{
    private static void Main()
    {
        try
        {
            using var reader = new GameMemoryReader();
            reader.StartBackgroundScanning();

            Console.WriteLine($"aces.exe base: 0x{reader.ModuleBase:X}");
            Console.WriteLine($"ground vtable:   0x{reader.VtableAddress:X}");
            Console.WriteLine($"aircraft vtable: 0x{reader.AircraftVtableAddress:X}");
            Console.WriteLine("Scanner запущен.");

            using var overlay = new WarThunderOverlay(reader);
            overlay.Start();

            Console.WriteLine("Overlay запущен. F10 включает/выключает ESP. Нажмите Enter для выхода...");
            Console.ReadLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка: {ex}");
            Console.ReadLine();
        }
    }

}

