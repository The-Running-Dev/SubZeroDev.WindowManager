using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.TestPlugin;

/// <summary>
/// The <c>faulty</c> scenario's switch files, read from the plugin's data folder every 50 ms:
/// <list type="bullet">
/// <item><c>crash</c> holds a count; the worker takes one off (deleting the file at zero) and exits
/// with code 9 a second after its view was set, so a count of N crashes N workers in a row. Each exit
/// appends a line to <c>exited.log</c>.</item>
/// <item><c>hang</c>: once it has answered a ping, the worker deletes it, writes when it last answered a ping to <c>hang-at</c>
/// (UTC Unix ms) and stops answering pings.</item>
/// <item><c>burst</c>: the worker deletes it and sends 1000 state patches as fast as it can, the last
/// setting "burst-final".</item>
/// <item><c>flood</c>: the worker deletes it and sends about 1000 state patches a second until it ends.</item>
/// </list>
/// </summary>
internal static class Switches
{
    public static async Task RunAsync(Surface surface)
    {
        var viewSet = Environment.TickCount64;
        var folder = Program.DataDirectory;
        while (true)
        {
            await Task.Delay(50).ConfigureAwait(false);
            var crash = Path.Combine(folder, "crash");
            if (File.Exists(crash) && Environment.TickCount64 - viewSet >= 1000)
            {
                var left = int.Parse(File.ReadAllText(crash).Trim(), System.Globalization.CultureInfo.InvariantCulture) - 1;
                if (left <= 0)
                {
                    File.Delete(crash);
                }
                else
                {
                    File.WriteAllText(crash, left.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                File.AppendAllText(Path.Combine(folder, "exited.log"),
                    $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} {Environment.ProcessId}{Environment.NewLine}");
                Environment.Exit(9);
            }

            if (PluginRuntime.LastPingAnswered != 0 && Take(Path.Combine(folder, "hang")))
            {
                PluginRuntime.AnswerPings = false;
                File.WriteAllText(Path.Combine(folder, "hang-at"),
                    PluginRuntime.LastPingAnswered.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            if (Take(Path.Combine(folder, "burst")))
            {
                for (var i = 0; i < 999; i++)
                {
                    surface.SetState("t", $"burst-{i}");
                }

                surface.SetState("t", "burst-final");
            }

            if (Take(Path.Combine(folder, "flood")))
            {
                for (var i = 0; ; i++)
                {
                    for (var j = 0; j < 10; j++)
                    {
                        surface.SetState("t", $"flood-{i}-{j}");
                    }

                    await Task.Delay(10).ConfigureAwait(false);
                }
            }
        }
    }

    private static bool Take(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }
}
