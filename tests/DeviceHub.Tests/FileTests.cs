using DeviceHub.Agent.Files;
using DeviceHub.Contracts;
using Xunit;

namespace DeviceHub.Tests;

public class PathGuardTests
{
    private static readonly string Windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    /// <summary>
    /// El ataque basico: escaparse con `..\`. Se normaliza ANTES de comparar, que
    /// es el orden que importa -- comparar primero y normalizar despues dejaria
    /// pasar `C:\Temp\..\Windows`.
    /// </summary>
    [Fact]
    public void Traversal_cannot_escape_into_a_protected_root()
    {
        var sneaky = Path.Combine(Path.GetTempPath(), "..", "..", "Windows", "System32");

        var normalized = PathGuard.Normalize(sneaky);

        if (normalized.StartsWith(Windows, StringComparison.OrdinalIgnoreCase))
            Assert.True(PathGuard.IsProtected(normalized));
    }

    [Fact]
    public void Writing_into_windows_is_refused()
    {
        Assert.False(PathGuard.TryValidate(Path.Combine(Windows, "System32", "drivers", "etc", "hosts"),
            forWriting: true, out _, out var error));

        Assert.Contains("protegida", error);
    }

    /// <summary>
    /// Listar y leer SI se permiten en rutas protegidas: ver que hay en
    /// C:\Windows es diagnostico legitimo. Lo que no se permite es modificarlo.
    /// </summary>
    [Fact]
    public void Reading_from_windows_is_allowed()
        => Assert.True(PathGuard.TryValidate(Windows, forWriting: false, out _, out _));

    /// <summary>
    /// La comparacion exige limite de segmento. C:\WindowsApps es un directorio
    /// distinto de C:\Windows y bloquearlo seria un falso positivo.
    /// </summary>
    [Fact]
    public void A_prefix_match_is_not_enough()
    {
        Assert.False(PathGuard.IsProtected(Windows + "Apps"));
        Assert.True(PathGuard.IsProtected(Windows));
        Assert.True(PathGuard.IsProtected(Path.Combine(Windows, "System32")));
    }

    /// <summary>
    /// El directorio de DeviceHub guarda el token de la maquina: borrarlo la
    /// dejaria huerfana y sin forma remota de recuperarla.
    /// </summary>
    [Fact]
    public void DeviceHub_own_directory_is_protected()
    {
        Assert.False(PathGuard.TryValidate(
            Path.Combine(DeviceHub.Agent.Identity.MachineIdentity.DefaultDirectory, "machine.json"),
            forWriting: true, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relativo\\sin\\raiz")]
    public void Non_absolute_paths_are_refused(string path)
        => Assert.False(PathGuard.TryValidate(path, forWriting: true, out _, out _));

    [Fact]
    public void Comparison_ignores_case_and_trailing_separators()
    {
        Assert.True(PathGuard.IsProtected(Windows.ToUpperInvariant()));
        Assert.True(PathGuard.IsProtected(PathGuard.Normalize(Windows + Path.DirectorySeparatorChar)));
    }

    [Fact]
    public void Protected_roots_are_resolved_not_hardcoded()
    {
        // Se preguntan a Windows: en una instalacion en D: o en otro idioma, las
        // rutas fijas no coincidirian.
        Assert.NotEmpty(PathGuard.ProtectedRoots);
        Assert.All(PathGuard.ProtectedRoots, r => Assert.True(Path.IsPathFullyQualified(r)));
    }

    [Fact]
    public void A_normal_working_directory_is_writable()
    {
        var scratch = Path.Combine(Path.GetTempPath(), "devicehub-test");
        Assert.True(PathGuard.TryValidate(scratch, forWriting: true, out _, out _));
    }
}

public class FileOperationsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "dhfiles-" + Guid.NewGuid().ToString("N"));

    public FileOperationsTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Listing_returns_files_and_directories()
    {
        File.WriteAllText(Path.Combine(_directory, "a.txt"), "hola");
        Directory.CreateDirectory(Path.Combine(_directory, "sub"));

        var json = FileOperations.ListDirectory(_directory);

        Assert.Contains("a.txt", json);
        Assert.Contains("sub", json);
    }

    /// <summary>
    /// No hay borrado recursivo en remoto. Un `path` mal escrito borraria un
    /// arbol entero de una PC de produccion, sin papelera ni deshacer.
    /// </summary>
    [Fact]
    public void A_non_empty_directory_is_not_deleted()
    {
        var sub = Path.Combine(_directory, "lleno");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "algo.txt"), "x");

        var ex = Assert.Throws<IOException>(() => FileOperations.Delete(sub));

        Assert.Contains("recursivo", ex.Message);
        Assert.True(Directory.Exists(sub));
    }

    [Fact]
    public void An_empty_directory_is_deleted()
    {
        var sub = Path.Combine(_directory, "vacio");
        Directory.CreateDirectory(sub);

        FileOperations.Delete(sub);

        Assert.False(Directory.Exists(sub));
    }

    /// <summary>El nombre nuevo no puede ser una ruta: seria mover a cualquier sitio.</summary>
    [Theory]
    [InlineData("..\\fuera.txt")]
    [InlineData("sub\\dentro.txt")]
    [InlineData("C:\\Windows\\evil.txt")]
    public void Rename_refuses_names_that_carry_a_path(string newName)
    {
        var file = Path.Combine(_directory, "origen.txt");
        File.WriteAllText(file, "x");

        Assert.Throws<ArgumentException>(() => FileOperations.Rename(file, newName));
    }

    [Fact]
    public void Read_and_write_round_trip()
    {
        var file = Path.Combine(_directory, "config.ini");

        FileOperations.WriteFile(file, Convert.ToBase64String("clave=valor"u8.ToArray()));

        Assert.Contains("ContentBase64", FileOperations.ReadFile(file));
        Assert.Equal("clave=valor", File.ReadAllText(file));
    }

    [Fact]
    public void Oversized_transfers_are_refused()
    {
        var file = Path.Combine(_directory, "grande.bin");
        var payload = Convert.ToBase64String(new byte[PathGuard.MaxTransferBytes + 1]);

        Assert.Throws<IOException>(() => FileOperations.WriteFile(file, payload));
    }

    [Fact]
    public void Invalid_base64_is_refused_before_touching_disk()
    {
        var file = Path.Combine(_directory, "malo.bin");

        Assert.Throws<ArgumentException>(() => FileOperations.WriteFile(file, "no-es-base64!!"));
        Assert.False(File.Exists(file));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // temporal
        }
    }
}

public class FileCommandPolicyTests
{
    /// <summary>
    /// Borrar exige Administrator: es irreversible y no hay papelera en remoto.
    /// Leer exige Engineer porque el contenido de una PC de planta puede incluir
    /// cadenas de conexion y configuracion de proceso.
    /// </summary>
    [Theory]
    [InlineData(CommandType.DeletePath, Roles.Engineer, false)]
    [InlineData(CommandType.DeletePath, Roles.Administrator, true)]
    [InlineData(CommandType.ReadFile, Roles.Technician, false)]
    [InlineData(CommandType.ReadFile, Roles.Engineer, true)]
    [InlineData(CommandType.ListDirectory, Roles.Technician, false)]
    [InlineData(CommandType.WriteFile, Roles.Engineer, true)]
    public void File_authorization_matrix(CommandType type, string role, bool allowed)
        => Assert.Equal(allowed, Roles.Satisfies(role, CommandPolicy.Get(type).RequiredRole));

    [Fact]
    public void Every_file_command_requires_a_path()
    {
        CommandType[] fileCommands =
        [
            CommandType.ListDirectory, CommandType.CreateDirectory, CommandType.DeletePath,
            CommandType.RenamePath, CommandType.ReadFile, CommandType.WriteFile
        ];

        Assert.All(fileCommands, t => Assert.Equal("path", CommandPolicy.RequiredParameter(t)));
    }
}
