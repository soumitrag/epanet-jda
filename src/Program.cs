namespace Demand_Gen;

static class Program
{
    /// <summary>
    ///  The main entry point for the application.
    /// </summary>
    [STAThread]
    static void Main()
    {
        // Initialize Windows Forms defaults, then run the main window on its required STA thread.
        ApplicationConfiguration.Initialize();
        Application.Run(new Form1());
    }    
}