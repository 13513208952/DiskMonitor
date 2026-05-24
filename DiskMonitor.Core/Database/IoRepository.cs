using DiskMonitor.Core.Models;
using Microsoft.Data.Sqlite;

namespace DiskMonitor.Core.Database;

public sealed class IoRepository(DatabaseManager db)
{
    // 批量追加当日 I/O（UPSERT：已有则累加，否则插入）
    public void UpsertIoBatch(IEnumerable<IoRecord> records)
    {
        using var conn = db.OpenConnection();
        using var tx   = conn.BeginTransaction();

        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO daily_io
                (date, process_name, process_path, drive_letter, volume_label,
                 volume_guid, disk_number, disk_model, read_bytes, write_bytes)
            VALUES
                ($date, $name, $path, $letter, $label,
                 $guid, $disk, $model, $read, $write)
            ON CONFLICT(date, process_name, process_path, volume_guid)
            DO UPDATE SET
                read_bytes  = read_bytes  + excluded.read_bytes,
                write_bytes = write_bytes + excluded.write_bytes,
                drive_letter = excluded.drive_letter,
                disk_number  = excluded.disk_number;
            """;

        var pDate   = cmd.Parameters.Add("$date",   SqliteType.Text);
        var pName   = cmd.Parameters.Add("$name",   SqliteType.Text);
        var pPath   = cmd.Parameters.Add("$path",   SqliteType.Text);
        var pLetter = cmd.Parameters.Add("$letter", SqliteType.Text);
        var pLabel  = cmd.Parameters.Add("$label",  SqliteType.Text);
        var pGuid   = cmd.Parameters.Add("$guid",   SqliteType.Text);
        var pDisk   = cmd.Parameters.Add("$disk",   SqliteType.Integer);
        var pModel  = cmd.Parameters.Add("$model",  SqliteType.Text);
        var pRead   = cmd.Parameters.Add("$read",   SqliteType.Integer);
        var pWrite  = cmd.Parameters.Add("$write",  SqliteType.Integer);

        foreach (var r in records)
        {
            pDate.Value   = r.Date;
            pName.Value   = r.ProcessName;
            pPath.Value   = r.ProcessPath;
            pLetter.Value = r.DriveLetter;
            pLabel.Value  = r.VolumeLabel;
            pGuid.Value   = r.VolumeGuid;
            pDisk.Value   = r.DiskNumber;
            pModel.Value  = r.DiskModel;
            pRead.Value   = r.ReadBytes;
            pWrite.Value  = r.WriteBytes;
            cmd.ExecuteNonQuery();
        }

        tx.Commit();
    }

    // 查询指定日期范围的记录（用于前端展示和 CSV 导出）
    public List<IoRecord> QueryByDateRange(string fromDate, string toDate)
    {
        using var conn = db.OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            SELECT date, process_name, process_path, drive_letter, volume_label,
                   volume_guid, disk_number, disk_model, read_bytes, write_bytes
            FROM   daily_io
            WHERE  date BETWEEN $from AND $to
            ORDER  BY date DESC, read_bytes + write_bytes DESC;
            """;
        cmd.Parameters.AddWithValue("$from", fromDate);
        cmd.Parameters.AddWithValue("$to",   toDate);

        var results = new List<IoRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new IoRecord
            {
                Date        = reader.GetString(0),
                ProcessName = reader.GetString(1),
                ProcessPath = reader.GetString(2),
                DriveLetter = reader.GetString(3),
                VolumeLabel = reader.GetString(4),
                VolumeGuid  = reader.GetString(5),
                DiskNumber  = reader.GetInt32(6),
                DiskModel   = reader.GetString(7),
                ReadBytes   = reader.GetInt64(8),
                WriteBytes  = reader.GetInt64(9),
            });
        }
        return results;
    }

    // 服务心跳
    public void UpdateHeartbeat()
    {
        using var conn = db.OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM service_status;
            INSERT INTO service_status (updated_at) VALUES ($ts);
            """;
        cmd.Parameters.AddWithValue("$ts", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    public DateTime? GetLastHeartbeat()
    {
        using var conn = db.OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = "SELECT updated_at FROM service_status LIMIT 1;";
        var val = cmd.ExecuteScalar();
        if (val is null or DBNull) return null;
        return DateTime.Parse((string)val, null, System.Globalization.DateTimeStyles.RoundtripKind);
    }

    // 卷快照 upsert
    public void UpsertVolumeSnapshot(Models.VolumeInfo v)
    {
        using var conn = db.OpenConnection();
        using var cmd  = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO volume_snapshots
                (volume_guid, drive_letter, volume_label, disk_number, disk_model, first_seen, last_seen)
            VALUES ($guid, $letter, $label, $disk, $model, $now, $now)
            ON CONFLICT(volume_guid) DO UPDATE SET
                drive_letter = excluded.drive_letter,
                volume_label = excluded.volume_label,
                disk_number  = excluded.disk_number,
                last_seen    = excluded.last_seen;
            """;
        var now = DateTime.UtcNow.ToString("O");
        cmd.Parameters.AddWithValue("$guid",   v.VolumeGuid);
        cmd.Parameters.AddWithValue("$letter", v.DriveLetter);
        cmd.Parameters.AddWithValue("$label",  v.VolumeLabel);
        cmd.Parameters.AddWithValue("$disk",   v.DiskNumber);
        cmd.Parameters.AddWithValue("$model",  v.DiskModel);
        cmd.Parameters.AddWithValue("$now",    now);
        cmd.ExecuteNonQuery();
    }
}
