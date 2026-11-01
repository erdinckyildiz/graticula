using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using OSGeo.OGR;
using OSGeo.OSR;
using Gdal = OSGeo.GDAL.Gdal;

namespace Graticula.Import.Reader;

/// <summary>
/// Loads GDAL from <c>gdal/&lt;rid&gt;/</c> beside this program and points it at its own data — what
/// <c>MaxRev.Gdal.Core</c>'s <c>GdalBase.ConfigureAll</c> did before ADR-254.
/// </summary>
/// <remarks>
/// <para>
/// <b>[ADR-254]: GDAL without Poppler.</b> Owner decision 2026-11-01 on D-380, <i>"PDF'siz GDAL'a geç"</i>.
/// MaxRev's build linked Poppler (GPL-2.0-or-later) into <c>libgdal</c> and into every SWIG wrapper, so it
/// could not be left out file by file. The payload is now conda-forge's <c>libgdal-core</c>, the four plugins
/// ADR-157's formats need, and <c>gdal-csharp</c>'s wrappers — pinned by
/// <c>deploy/gdal/gdal-natives.lock.json</c> and laid out by <c>tools/gdal-natives.py</c>, which refuses a
/// closure with a GPL library in it. The C# binding is GDAL's own SWIG output, compiled into this assembly.
/// </para>
/// <para>
/// <b>Why a resolver rather than the default probe.</b> The binding imports <c>gdal_wrap</c>,
/// <c>ogr_wrap</c>, <c>osr_wrap</c> and <c>gdalconst_wrap</c>; the libraries live in one directory per
/// platform, so the resolver loads each from there by its full path. <c>libgdal</c> is loaded first, so a
/// wrapper's dependency on it is met by the library already in the process, and the rest of GDAL's closure is
/// found by its own <c>$ORIGIN</c> run path on Linux and by the directory added to <c>PATH</c> on Windows.
/// </para>
/// <para>
/// <b>Plugins from one directory and no other.</b> <c>GDAL_DRIVER_PATH</c> names this payload's
/// <c>gdalplugins</c>, so a PDF plugin installed elsewhere on a host is never loaded into this process.
/// </para>
/// </remarks>
internal static class GdalNative
{
    private static readonly string[] Wrappers = ["gdal_wrap", "ogr_wrap", "osr_wrap", "gdalconst_wrap"];

    /// <summary>The loaded <c>libgdal</c>, for the one C function the binding does not wrap.</summary>
    public static IntPtr Library { get; private set; }

    /// <summary>The platform's directory: <c>gdal/linux-x64</c>, <c>gdal/linux-arm64</c> or <c>gdal/win-x64</c>.</summary>
    public static string Root { get; private set; } = string.Empty;

    /// <summary>Loads GDAL and registers its drivers. Once, before any other GDAL call.</summary>
    public static void Configure()
    {
        string rid = (OperatingSystem.IsWindows() ? "win-" : OperatingSystem.IsLinux() ? "linux-" : "osx-")
            + RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Root = Path.Combine(AppContext.BaseDirectory, "gdal", rid);
        bool windows = OperatingSystem.IsWindows();
        string libraries = Path.Combine(Root, windows ? "bin" : "lib");

        if (!Directory.Exists(libraries))
        {
            throw new InvalidOperationException(
                $"GDAL for {rid} is not at '{libraries}'. tools/gdal-natives.py lays it out at build time (ADR-254); "
                + "this platform has no entry in deploy/gdal/gdal-natives.lock.json, or the build did not run it.");
        }

        if (windows)
        {
            // A DLL's own imports are searched for on PATH, not beside it.
            Environment.SetEnvironmentVariable(
                "PATH", libraries + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
        }

        string data = Path.Combine(Root, "share", "proj");
        Environment.SetEnvironmentVariable("PROJ_DATA", data);
        Environment.SetEnvironmentVariable("PROJ_LIB", data);

        Library = NativeLibrary.Load(Path.Combine(libraries, windows ? "gdal.dll" : "libgdal.so.39"));

        Dictionary<string, IntPtr> loaded = new(StringComparer.Ordinal);
        NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
        {
            if (Array.IndexOf(Wrappers, name) < 0)
            {
                return IntPtr.Zero;
            }

            lock (loaded)
            {
                if (!loaded.TryGetValue(name, out IntPtr handle))
                {
                    handle = NativeLibrary.Load(Path.Combine(libraries, windows ? $"{name}.dll" : $"lib{name}.so"));
                    loaded[name] = handle;
                }

                return handle;
            }
        });

        Gdal.SetConfigOption("GDAL_DATA", Path.Combine(Root, "share", "gdal"));
        Gdal.SetConfigOption("GDAL_DRIVER_PATH", Path.Combine(libraries, "gdalplugins"));
        Osr.SetPROJSearchPaths([data]);
        Gdal.AllRegister();
        Ogr.RegisterAll();
    }
}
