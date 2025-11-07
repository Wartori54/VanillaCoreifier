using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Microsoft.NET.HostModel.AppHost;
using Mono.Cecil;
using Newtonsoft.Json.Linq;

namespace VanillaCoreifier;

internal static class Program {
    private static InstallPlatform Platform;

    private static IEverestPath ObtainPathFromArgs(string[] args) {
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--remote":
                     return new RemoteEverestPath();
                case "--everestpath":
                    return new LocalEverestPath(ConsumeNextArg(args, ref i));
            }
        }

        return new LocalEverestPath(".."); // Default to parent strategically
    }

    private static string ConsumeNextArg(string[] args, ref int i) {
        if (i + 1 >= args.Length) throw new Exception($"Expected path after flag at index {i}!");
        i++;
        return args[i];
    }

    public static void Main(string[] args) {
        string inputPath = "./Celeste.exe";
        string outputFile = "CelesteVCore.dll";
        string forcedPlatform = "";
        for (int i = 0; i < args.Length; i++) {
            switch (args[i]) {
                case "--input":
                    inputPath = ConsumeNextArg(args, ref i);
                    break;
                case "--output":
                    outputFile = ConsumeNextArg(args, ref i);
                    break;
                case "--platform":
                    forcedPlatform = ConsumeNextArg(args, ref i);
                    break;
            }
        }

        // if (Path.GetDirectoryName(Path.GetFullPath(inputPath)) != Path.GetDirectoryName(Path.GetFullPath(outputPath)))
        //     throw new Exception("Input path and output path must be in the same directory!");

        using IEverestPath everestPath = ObtainPathFromArgs(args);
        string inputDir = Path.GetFullPath(Path.GetDirectoryName(inputPath)!);
        string outputPath = Path.Combine(inputDir, outputFile);
        
        // TODO: XNA-FNA relink
        if (forcedPlatform != "") {
            if (!Enum.TryParse(forcedPlatform, true, out InstallPlatform res)) {
                throw new InvalidEnumArgumentException($"Could not parse {forcedPlatform} as a platform!");
            }

            Platform = res;
        } else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Platform = InstallPlatform.Windows;
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            Platform = InstallPlatform.Linux;
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            Platform = InstallPlatform.MacOS;
        else
            throw new InvalidOperationException();
        
        everestPath.CopyDirTo("everest-lib", inputDir);
        
        Dictionary<string, string> fileNameMap = new();
        List<string> sourceLibs;
        string libTarget;
        switch (Platform) {
            case InstallPlatform.Windows: {
                if (RuntimeInformation.ProcessArchitecture == Architecture.X64) {
                    libTarget = "lib64-win-x64";
                } else {
                    libTarget = "lib64-win-x86";
                }

                fileNameMap["fmodstudio64.dll"] = "fmodstudio.dll";
                sourceLibs = Directory.GetFiles(inputDir).Where(p => p.EndsWith(".dll")).ToList();
            } break;
            // I'll just hardcode these instead of parsing the .configs, i don't think this is going to change ever
            case InstallPlatform.Linux: {
                libTarget = "lib64-linux";
                fileNameMap["libfmod.so.10"] = "libfmod.so";
                fileNameMap["libfmodstudio.so.10"] = "libfmodstudio.so";
                fileNameMap["libSDL2-2.0.so.0"] = "libSDL2.so";
                fileNameMap["libFNA3D.so.0"] = "libFNA3D.so";
                fileNameMap["libFAudio.so.0"] = "libFAudio.so";
                sourceLibs = Directory.GetFiles(Path.Combine(inputDir, "lib64")).ToList();
            } break;
            case InstallPlatform.MacOS: {
                libTarget = "lib64-osx";
                fileNameMap["libSDL2-2.0.0.dylib"] = "libSDL2.dylib";
                fileNameMap["libFNA3D.0.dylib"] = "libFNA3D.dylib";
                fileNameMap["libFAudio.0.dylib"] = "libFAudio.dylib";
                sourceLibs = Directory.GetFiles(Path.Combine(inputDir, "..", "MacOS", "osx")).ToList();
            } break;
            default: throw new ArgumentOutOfRangeException();
        }
        
        string[] everestLibs = Directory.GetFiles(Path.Combine(inputDir, "everest-lib",  libTarget));
        
        for (int i = 0; i < everestLibs.Length; i++) {
            bool replaced = false;
            for (int j = 0; j < sourceLibs.Count; j++) {
                if (Path.GetFileName(sourceLibs[j]) == Path.GetFileName(everestLibs[i])) {
                    sourceLibs[j] = everestLibs[i];
                    replaced = true;
                    break;
                }
            }
            if (replaced) continue;
            sourceLibs.Add(everestLibs[i]);
        }

        foreach (string file in sourceLibs) {
            string dst = Path.GetFileName(file);
            if (fileNameMap.TryGetValue(dst, out string? newDst)) {
                dst = newDst;
            }
            if (file == Path.Combine(inputDir, dst)) continue; // This may happen for windows as we target too many dlls
            File.Copy(file, Path.Combine(inputDir, dst), true);
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) { // Linux requires the symlinks to the original file names
                string symlinkDest = Path.Combine(inputDir, Path.GetFileName(file));
                if (!File.Exists(symlinkDest)) {
                    File.CreateSymbolicLink(symlinkDest,
                        Path.Combine(inputDir, Path.GetFileName(dst)));
                }
            }
        }
        
        everestPath.CopyFileTo(Path.Combine("everest-lib", "FNA.dll"), inputDir);
        everestPath.CopyFileTo(Path.Combine("everest-lib", "FNA.pdb"), inputDir);
        
        Assembly coreifierAsm = everestPath.LoadCoreifier(inputDir);

        Type coreifierType = coreifierAsm.GetTypeSafe("NETCoreifier.Coreifier");
        MethodBase coreifyMethod = coreifierType.GetMethodPSSafe("ConvertToNetCore", [typeof(string), typeof(string)]);

        // DO NOT THE PDB!!!!1!
        // In a more serious note for some reason cecil freaks out and tries to use the unmanaged pdb symbol writer
        // if it can find the associated pdb for the module (celeste.exe in our case) as such it will crash (at least on not windows)
        // Everest also does not keep that file when converting to core
        // Consequently, this amazing hack to just make it look like it's not there to cecil will live in here.
        string exePdb = Path.ChangeExtension(inputPath, ".pdb");
        if (File.Exists(exePdb)) {
            File.Move(exePdb, exePdb + "1");
        }
        coreifyMethod.Invoke(null, [inputPath, outputPath]);
        
        if (File.Exists(exePdb + "1")) {
            File.Move(exePdb + "1", exePdb);
        }
        
        // Relink to FNA if necessary
        RelinkToFNA(outputPath, Path.Combine(inputDir, "FNA.dll")); // FNA.dll should be there already from the library copy
        
        Assembly miniinstallerAsm = everestPath.LoadMiniInstaller();

        Type miniinstallerProgramType = miniinstallerAsm.GetTypeSafe("MiniInstaller.LibAndDepHandling", "MiniInstaller.Program");
        MethodBase runtimeJsonMethod =
            miniinstallerProgramType.GetMethodPSSafe("CreateRuntimeConfigFiles", [typeof(string), typeof(string[])]);
        runtimeJsonMethod.Invoke(null, [outputPath, null]);

        SetupAppHosts(everestPath, inputPath, outputPath, outputPath);
        
        
    }
    
    // Effectively copied from MiniInstaller (https://github.com/EverestAPI/Everest/blob/b5b247eb37b103a37f61d4df748fa2eba951a424/MiniInstaller/Program.cs#L877)
    private static void SetupAppHosts(IEverestPath everestPath, string appExe, string appDll, string? resDll = null) {
        string outputDir = Path.GetDirectoryName(appDll)!;
        string inputDir = Path.GetDirectoryName(appExe)!;
        // We only support setting copying the host resources on Windows
        // don't use `Platform` here since it may hold an arbitrary value and this copy is only possible when patching
        // under windows
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            resDll = null;

        // (Do not) Delete MonoKickstart files
        // File.Delete(Path.ChangeExtension(appExe, ".bin.x86"));
        // File.Delete(Path.ChangeExtension(appExe, ".bin.x86_64"));
        // File.Delete($"{appExe}.config");
        // File.Delete(Path.Combine(Path.GetDirectoryName(appExe)!, "monoconfig"));
        // File.Delete(Path.Combine(Path.GetDirectoryName(appExe)!, "monomachineconfig"));
        // File.Delete(Path.Combine(Path.GetDirectoryName(appExe)!, "FNA.dll.config"));

        string hostsDir = everestPath.GetApphostsPath();

        switch (Platform) {
            case InstallPlatform.Windows: {
                // Bind Windows apphost
                Console.WriteLine($"Binding Windows {(Environment.Is64BitOperatingSystem ? "64" : "32")} bit apphost {appExe}");
                HostWriter.CreateAppHost(
                    Path.Combine(hostsDir, $"win.{(Environment.Is64BitOperatingSystem ? "x64" : "x86")}.exe"),
                    appExe, Path.GetRelativePath(outputDir, appDll),
                    assemblyToCopyResorcesFrom: resDll,
                    windowsGraphicalUserInterface: true
                );
            } break;
            case InstallPlatform.Linux: {
                // Bind Linux apphost
                Console.WriteLine($"Binding Linux apphost {Path.ChangeExtension(appExe, null) + "_host"}");
                HostWriter.CreateAppHost(Path.Combine(hostsDir, "linux"), Path.ChangeExtension(appExe, null) + "_host",
                    Path.GetRelativePath(outputDir, appDll));
                WriteLDLIBWrapper(Path.Combine(Path.GetDirectoryName(appDll)!, Path.ChangeExtension(appExe, null)));
            } break;
            case InstallPlatform.MacOS: {
                // Bind OS X apphost
                Console.WriteLine($"Binding OS X apphost {Path.ChangeExtension(appExe, null) + "_host"}");
                HostWriter.CreateAppHost(Path.Combine(hostsDir, "osx"), Path.ChangeExtension(appExe, null) + "_host",
                    Path.GetRelativePath(outputDir, appDll));

                string pathOsxExecDir = Path.Combine(inputDir, "..", "MacOS");
                // File.Delete(Path.Combine(pathOsxExecDir, Path.GetFileNameWithoutExtension(appExe)));
                File.CreateSymbolicLink(Path.Combine(pathOsxExecDir, Path.GetFileNameWithoutExtension(appExe) + "_host"), Path.ChangeExtension(appDll, null) + "_host");
                WriteLDLIBWrapper(Path.Combine(pathOsxExecDir, Path.GetFileNameWithoutExtension(appExe)));
            } break;
            default:
                throw new ArgumentOutOfRangeException();
        }
        
        // Finally copy the piton-runtime.yaml
        everestPath.CopyFileTo(Path.Combine("everest-lib", "piton-runtime.yaml"), outputDir);
    }

    // Why should this not be inlined, well, basically because we wont be able to resolve cecil until the everest path
    // is made, as such the compilation of the calling method must not trigger this method getting checked, and consequently
    // trying to load cecil
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RelinkToFNA(string outputPath, string fnaPath) {
        Console.WriteLine("Attempting to relink to FNA.");
        ModuleDefinition modDef = ModuleDefinition.ReadModule(outputPath, new ReaderParameters(ReadingMode.Immediate));
        // Check first to possibly skip the fna dll load
        List<int> toRemove = [];
        for (int i = 0; i < modDef.AssemblyReferences.Count; i++) {
            Console.WriteLine(modDef.AssemblyReferences[i].Name);
            if (!modDef.AssemblyReferences[i].Name.StartsWith("Microsoft.Xna.Framework")) continue;
            toRemove.Add(i);
        }

        if (toRemove.Count != 0) {
            Console.WriteLine($"Relinking {toRemove.Count} references.");
            // Load fna as late as possible
            ModuleDefinition fnaDef = ModuleDefinition.ReadModule(fnaPath, new ReaderParameters(ReadingMode.Deferred));
            modDef.AssemblyReferences.Add(fnaDef.Assembly.Name);
            // Removing will shift prior elements, so remove last first
            toRemove.Reverse();
            foreach (int i in toRemove) {
                modDef.AssemblyReferences.RemoveAt(i);
            }
            // Use a different name to evade file locks
            modDef.Write(outputPath + "1");
            modDef.Dispose();
            File.Move(outputPath + "1", outputPath, true);
        }
    }

    private static void WriteLDLIBWrapper(string dest) {
        using FileStream file = File.Open(dest, FileMode.Truncate, FileAccess.Write);
        using StreamWriter writer = new(file);
        writer.WriteLine("#!/bin/bash");
        writer.WriteLine("cd \"`dirname \"$0\"`\"");
        writer.WriteLine("if [ \"$UNAME\" == \"Darwin\" ]; then");
        writer.WriteLine("    export DYLD_LIBRARY_PATH=\"$DYLD_LIBRARY_PATH:.\"");
        writer.WriteLine("else");
        writer.WriteLine("    export LD_LIBRARY_PATH=\"$LD_LIBRARY_PATH:.\"");
        writer.WriteLine("fi");
        writer.WriteLine($"./{Path.GetFileName(dest)}_host");
    }
    
    private static void CopyDirTo(string dir, string dest) {
        string targetNested = Path.Combine(dest, Path.GetFileName(dir)!);
        Directory.CreateDirectory(targetNested);
        CopyDirToInner(dir, targetNested);
    }

    private static void CopyDirToInner(string dir, string dest) {
        foreach (string nestedDir in Directory.GetDirectories(dir)) {
            string nestedDest = Path.Combine(dest, Path.GetFileName(nestedDir));
            Directory.CreateDirectory(nestedDest);
            CopyDirToInner(nestedDir, nestedDest);
        }

        foreach (string file in Directory.GetFiles(dir)) {
            File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
        }
    }

    private enum InstallPlatform {
        Windows,
        Linux,
        MacOS
    }
    
    private interface IEverestPath : IDisposable {
        // Coreifier needs itself as a file, as such load it after copying to dest dir
        public Assembly LoadCoreifier(string dir);

        public Assembly LoadMiniInstaller();

        public string GetApphostsPath();

        public void CopyFileTo(string file, string loc);

        public void CopyDirTo(string dir, string dest);
    }
    
    private class LocalEverestPath : IEverestPath {

        public LocalEverestPath(string path) {
            Path = System.IO.Path.GetFullPath(path);
            AssemblyLoadContext.Default.Resolving += (context, name) => {
                string inEverest = System.IO.Path.Combine(Path, name.Name!) + ".dll";
                Console.WriteLine($"Checking for {inEverest}");
                return File.Exists(inEverest) ? Assembly.LoadFile(inEverest) : null;
            };
        }

        private string Path { get; }
        public Assembly LoadCoreifier(string dir) {
            CopyFileTo(System.IO.Path.Combine(Path, "NETCoreifier.dll"), System.IO.Path.Combine(dir));
            return Assembly.LoadFile(System.IO.Path.Combine(dir, "NETCoreifier.dll"));
        }

        public Assembly LoadMiniInstaller() {
            return Assembly.LoadFile(System.IO.Path.Combine(Path, "MiniInstaller.dll"));
        }

        public string GetApphostsPath() {
            return System.IO.Path.Combine(Path, "piton-apphosts");
        }

        public void CopyFileTo(string file, string loc) {
            File.Copy(System.IO.Path.Combine(Path, file), System.IO.Path.Combine(loc, System.IO.Path.GetFileName(file)), true);
        }

        public void CopyDirTo(string dir, string dest) {
            Program.CopyDirTo(System.IO.Path.Combine(Path, dir), dest);
        }

        public void Dispose() {
        }
    }

    private class RemoteEverestPath : IEverestPath {
        private readonly string zipPath;
        private readonly ZipArchive zipFile;
        private string? apphostsPath = null;

        public RemoteEverestPath() {
            using HttpClient client = new();
            // Download the file listings
            string? downloadUrl = null;
            using (Task<string> stream =
                   client.GetStringAsync("https://maddie480.ovh/celeste/everest-versions?supportsNativeBuilds=true")) {
                JArray json = JArray.Parse(stream.Result);
                int targetVer = 0;
                foreach (JToken entry in json) {
                    if (entry["branch"]!.Value<string>() != "stable") continue;
                    
                    int version = entry["version"]!.Value<int>();
                    if (targetVer >= version) continue;
                    
                    targetVer = version;
                    downloadUrl = entry["mainDownload"]!.Value<string>()!;
                }
            }

            if (downloadUrl == null) throw new Exception("Couldn't find latest stable everest version!");
            
            // Get a place
            zipPath = Path.GetTempFileName();
            // Download everest
            using (Task<Stream> stream = client.GetStreamAsync(downloadUrl))
            using (FileStream fs = new(zipPath, FileMode.Truncate)) {
                stream.Result.CopyTo(fs);
            }

            zipFile = ZipFile.OpenRead(zipPath);
            AssemblyLoadContext.Default.Resolving += (context, name) => {
                Console.WriteLine($"Checking for {name}");
                try {
                    return LoadAssembly(name.Name! + ".dll");
                } catch (FileNotFoundException) {
                    return null;
                }
            };
        }

        
        private bool TryGetZipEntryStream(string name, [NotNullWhen(true)] out Stream? stream) {
            ZipArchiveEntry? entry = zipFile.GetEntry("main/" + name); // Everything is inside a folder called main
            stream = entry?.Open();
            return stream != null;
        }

        private Stream GetZipEntryStream(string name) {
            if (TryGetZipEntryStream(name, out Stream? stream))
                return stream;
            throw new FileNotFoundException($"Could not find {name}!");
        }

        private Assembly LoadAssembly(string name) {
            using Stream stream = GetZipEntryStream(name);
            using MemoryStream tmpStream = new();
            stream.CopyTo(tmpStream); // This is awful, but the loader needs the file length
            tmpStream.Position = 0;

            
            if (!TryGetZipEntryStream(Path.ChangeExtension(name, ".pdb"), out Stream? symbolStream)) {
                return AssemblyLoadContext.Default.LoadFromStream(tmpStream);
            }

            using Stream _ = symbolStream; // Hack to add `using` context to an object late
            using MemoryStream tmpSymbolStream = new();
            symbolStream.CopyTo(tmpSymbolStream);
            tmpSymbolStream.Position = 0;
            
            return AssemblyLoadContext.Default.LoadFromStream(tmpStream, tmpSymbolStream);

        }
        
        public Assembly LoadCoreifier(string dir) { // Coreifier reads itself with cecil, so we need a physical file ._.
            string coreifierPath = Path.Combine(dir, "NETCoreifier.dll");
            using Stream stream = GetZipEntryStream("NETCoreifier.dll");
            using (FileStream fs = new(coreifierPath, FileMode.Create)) {
                stream.CopyTo(fs);
            }

            if (TryGetZipEntryStream("NETCoreifier.pdb", out Stream? symStream)) {
                using Stream _ = symStream;
                using (FileStream fsSym = new(Path.ChangeExtension(coreifierPath, ".pdb"), FileMode.Create)) {
                    symStream.CopyTo(fsSym);
                }
            }
            return Assembly.LoadFile(coreifierPath);
        }

        public Assembly LoadMiniInstaller() {
            return LoadAssembly("MiniInstaller.dll");
        }

        public string GetApphostsPath() {
            if (apphostsPath == null) {
                apphostsPath = Path.GetTempFileName();
                File.Delete(apphostsPath);
                Directory.CreateDirectory(apphostsPath);

                CopyDirTo("piton-apphosts", apphostsPath);
            }

            return Path.Combine(apphostsPath, "piton-apphosts");
        }

        public void CopyFileTo(string file, string loc) {
            ZipArchiveEntry? entry = zipFile.GetEntry("main/" + file);
            if (entry == null) throw new Exception($"Could not find file {file}!");
            using FileStream fs = File.Open(Path.Combine(loc, Path.GetFileName(file)), FileMode.Create);
            entry.Open().CopyTo(fs);
        }

        public void CopyDirTo(string dir, string dest) {
            foreach (ZipArchiveEntry entry in zipFile.Entries) {
                if (entry.Name == "") continue; // It's a dir
                if (entry.FullName.Contains("main/" + dir)) {
                    string entryDir = Path.Combine(dest, Path.GetDirectoryName(entry.FullName["main/".Length..])!);
                    Directory.CreateDirectory(entryDir);
                    using FileStream fs = File.Open(Path.Combine(entryDir, entry.Name), FileMode.Create);
                    entry.Open().CopyTo(fs);
                }
            }
        }

        public void Dispose() {
            zipFile.Dispose();
            File.Delete(zipPath);
            if (apphostsPath != null) {
                Directory.Delete(apphostsPath, true);
            }

            apphostsPath = null;
        }
    }

    private static Type GetTypeSafe(this Assembly asm, string target, params string[] fallbacks) {
        return asm.GetType(target) ?? fallbacks.Select(asm.GetType).FirstOrDefault(t => t != null, null) ?? throw new Exception($"Could not find type {target} in assembly {asm.GetName()} ({asm.Location})");
    }

    private static MethodBase GetMethodPSSafe(this Type type, string name, Type[] args) {
        return type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, args) ?? throw new Exception(
            $"Could not find public-static method {name} in type {type.FullName} with arguments: {string.Join(", ", args.Select(a => a.FullName))}");
    }
}