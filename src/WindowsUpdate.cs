using System.Runtime.InteropServices;

namespace AirDesk.Companion;

internal static class WindowsUpdate
{
    public static string Search()
    {
        object? session = null, searcher = null, result = null, updates = null;
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session", throwOnError: true)!;
            session = Activator.CreateInstance(type)!;
            searcher = ((dynamic)session).CreateUpdateSearcher();
            result = ((dynamic)searcher).Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
            if ((int)((dynamic)result).ResultCode != 2) return "Windows Update n’a pas confirmé la recherche.";
            updates = ((dynamic)result).Updates;
            int count = ((dynamic)updates).Count;
            if (count == 0) return "Windows Update ne signale aucune mise à jour disponible.";

            var labels = new List<string>();
            for (int i = 0; i < Math.Min(count, 10); i++)
            {
                object update = ((dynamic)updates).Item(i);
                try { labels.Add((string)((dynamic)update).Title); }
                finally { Release(update); }
            }
            return $"{count} mise(s) à jour disponible(s) :\n" + string.Join("\n", labels);
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException)
        {
            return "Recherche indisponible. Vérifiez l’état de Windows Update.";
        }
        finally
        {
            Release(updates);
            Release(result);
            Release(searcher);
            Release(session);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance);
    }
}
