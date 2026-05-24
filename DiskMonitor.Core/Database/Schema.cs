namespace DiskMonitor.Core.Database;

internal static class Schema
{
    public const string CreateTables = """
        CREATE TABLE IF NOT EXISTS daily_io (
            id           INTEGER PRIMARY KEY,
            date         TEXT    NOT NULL,
            process_name TEXT    NOT NULL,
            process_path TEXT    NOT NULL DEFAULT '',
            drive_letter TEXT    NOT NULL DEFAULT '',
            volume_label TEXT    NOT NULL DEFAULT '',
            volume_guid  TEXT    NOT NULL,
            disk_number  INTEGER NOT NULL DEFAULT -1,
            disk_model   TEXT    NOT NULL DEFAULT '',
            read_bytes   INTEGER NOT NULL DEFAULT 0,
            write_bytes  INTEGER NOT NULL DEFAULT 0,
            UNIQUE(date, process_name, process_path, volume_guid)
        );
        CREATE INDEX IF NOT EXISTS idx_daily_io_date         ON daily_io(date);
        CREATE INDEX IF NOT EXISTS idx_daily_io_volume_guid  ON daily_io(volume_guid);
        CREATE INDEX IF NOT EXISTS idx_daily_io_process_name ON daily_io(process_name);

        CREATE TABLE IF NOT EXISTS service_status (
            id         INTEGER PRIMARY KEY,
            updated_at TEXT    NOT NULL
        );

        CREATE TABLE IF NOT EXISTS process_history (
            id         INTEGER PRIMARY KEY,
            pid        INTEGER NOT NULL,
            name       TEXT    NOT NULL,
            path       TEXT    NOT NULL DEFAULT '',
            start_time TEXT    NOT NULL,
            end_time   TEXT
        );

        CREATE TABLE IF NOT EXISTS volume_snapshots (
            id           INTEGER PRIMARY KEY,
            volume_guid  TEXT    NOT NULL UNIQUE,
            drive_letter TEXT    NOT NULL DEFAULT '',
            volume_label TEXT    NOT NULL DEFAULT '',
            disk_number  INTEGER NOT NULL DEFAULT -1,
            disk_model   TEXT    NOT NULL DEFAULT '',
            first_seen   TEXT    NOT NULL,
            last_seen    TEXT    NOT NULL
        );
        """;
}
