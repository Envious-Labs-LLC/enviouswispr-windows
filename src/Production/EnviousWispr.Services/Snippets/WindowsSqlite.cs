using System.Runtime.InteropServices;
using System.Text;

namespace EnviousWispr.Services.Snippets;

/// <summary>The few SQLite calls a read of another app's database needs, through the SQLite Windows itself ships.</summary>
/// <remarks>
/// WINDOWS' OWN <c>winsqlite3.dll</c> (System32, present on every supported Windows 10 and 11 build) rather than a
/// package with its own native library: the app reads one table out of one file, and a narrow interface over a
/// library the platform already carries adds nothing to install, sign or keep patched.
///
/// A TYPE THAT DRIFTS IS A REFUSAL, NOT A CONVERSION. SQLite columns are dynamically typed, so a changed schema can
/// hand back an integer where text belongs; converting it silently would turn a malformed source into plausible
/// snippets (macOS <c>SmartImportSQLiteReader</c>, strict column reads). Every read below throws
/// <see cref="SqliteReadException"/> on a type it did not ask for.
/// </remarks>
internal static class WindowsSqlite
{
    private const string Library = "winsqlite3";
    private const int SqliteOk = 0;
    private const int SqliteRow = 100;
    private const int SqliteDone = 101;
    private const int OpenReadWrite = 0x00000002;
    private const int ColumnInteger = 1;
    private const int ColumnText = 3;
    private const int ColumnNull = 5;

    [DllImport(Library, EntryPoint = "sqlite3_open_v2", ExactSpelling = true)]
    private static extern int OpenV2(byte[] fileName, out IntPtr db, int flags, IntPtr vfs);

    [DllImport(Library, EntryPoint = "sqlite3_close_v2", ExactSpelling = true)]
    private static extern int Close(IntPtr db);

    [DllImport(Library, EntryPoint = "sqlite3_exec", ExactSpelling = true)]
    private static extern int Exec(IntPtr db, byte[] sql, IntPtr callback, IntPtr argument, IntPtr errorMessage);

    [DllImport(Library, EntryPoint = "sqlite3_prepare_v2", ExactSpelling = true)]
    private static extern int Prepare(IntPtr db, byte[] sql, int length, out IntPtr statement, IntPtr tail);

    [DllImport(Library, EntryPoint = "sqlite3_step", ExactSpelling = true)]
    private static extern int Step(IntPtr statement);

    [DllImport(Library, EntryPoint = "sqlite3_finalize", ExactSpelling = true)]
    private static extern int Finalize(IntPtr statement);

    [DllImport(Library, EntryPoint = "sqlite3_column_type", ExactSpelling = true)]
    private static extern int ColumnType(IntPtr statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_column_text16", ExactSpelling = true)]
    private static extern IntPtr ColumnText16(IntPtr statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_column_bytes16", ExactSpelling = true)]
    private static extern int ColumnBytes16(IntPtr statement, int column);

    [DllImport(Library, EntryPoint = "sqlite3_column_int64", ExactSpelling = true)]
    private static extern long ColumnInt64(IntPtr statement, int column);

    /// <summary>Runs <paramref name="sql"/> against a PRIVATE COPY and maps every row; null from the mapper is a counted exclusion.</summary>
    /// <remarks>
    /// OPENED READ-WRITE, DELIBERATELY, AND ONLY EVER ON A COPY WE MADE (macOS #3032). A database whose header says
    /// WAL needs a <c>-shm</c> index before it can be read, and a read-only connection may not create one, so a
    /// store left in WAL mode without its sidecars would refuse. Two things keep it honest: no create flag, so a
    /// missing copy fails rather than becoming an empty database that imports as "nothing found"; and
    /// <c>PRAGMA query_only</c> straight after the open, so no statement can change a row. Only
    /// <c>SQLITE_DONE</c> means "that was all of them": a busy, corrupt or failing step is a refusal, never a
    /// partial list presented as a whole one.
    /// </remarks>
    public static (List<T> Rows, int Excluded) ReadRows<T>(string privateCopy, string sql, Func<Row, T?> map)
        where T : class
    {
        if (OpenV2(Utf8(privateCopy), out var db, OpenReadWrite, IntPtr.Zero) != SqliteOk)
        {
            _ = Close(db);
            throw new SqliteReadException();
        }

        try
        {
            if (Exec(db, Utf8("PRAGMA query_only=ON"), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != SqliteOk ||
                Prepare(db, Utf8(sql), -1, out var statement, IntPtr.Zero) != SqliteOk)
            {
                throw new SqliteReadException();
            }

            try
            {
                var rows = new List<T>();
                var excluded = 0;
                int result;
                while ((result = Step(statement)) == SqliteRow)
                {
                    if (map(new Row(statement)) is { } row)
                    {
                        rows.Add(row);
                    }
                    else
                    {
                        excluded++;
                    }
                }

                if (result != SqliteDone)
                {
                    throw new SqliteReadException();
                }

                return (rows, excluded);
            }
            finally
            {
                _ = Finalize(statement);
            }
        }
        finally
        {
            _ = Close(db);
        }
    }

    /// <summary>Writes through the same library, for tests that build a fixture database. Never used on another app's file.</summary>
    internal static void ExecuteOnNewDatabase(string path, string sql)
    {
        const int OpenCreate = 0x00000004;
        if (OpenV2(Utf8(path), out var db, OpenReadWrite | OpenCreate, IntPtr.Zero) != SqliteOk)
        {
            _ = Close(db);
            throw new SqliteReadException();
        }

        try
        {
            if (Exec(db, Utf8(sql), IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != SqliteOk)
            {
                throw new SqliteReadException();
            }
        }
        finally
        {
            _ = Close(db);
        }
    }

    private static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value + '\0');

    /// <summary>One row, read strictly by column.</summary>
    public readonly struct Row
    {
        private readonly IntPtr _statement;

        internal Row(IntPtr statement) => _statement = statement;

        public string RequiredText(int column) =>
            OptionalText(column) ?? throw new SqliteReadException();

        public string? OptionalText(int column)
        {
            var type = ColumnType(_statement, column);
            if (type == ColumnNull)
            {
                return null;
            }

            if (type != ColumnText)
            {
                throw new SqliteReadException();
            }

            var pointer = ColumnText16(_statement, column);
            var bytes = ColumnBytes16(_statement, column);
            return pointer == IntPtr.Zero ? throw new SqliteReadException() : Marshal.PtrToStringUni(pointer, bytes / 2);
        }

        /// <summary>A 0 or 1 stored as an integer; anything else is a schema this reader does not know.</summary>
        public bool RequiredBoolean(int column)
        {
            if (ColumnType(_statement, column) != ColumnInteger)
            {
                throw new SqliteReadException();
            }

            return ColumnInt64(_statement, column) switch
            {
                0 => false,
                1 => true,
                _ => throw new SqliteReadException(),
            };
        }
    }
}

/// <summary>The database could not be read safely and whole.</summary>
internal sealed class SqliteReadException : Exception
{
}
