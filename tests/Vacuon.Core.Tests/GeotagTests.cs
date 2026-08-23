using System.Diagnostics;
using Vacuon.Core.Preview;
using Xunit;

namespace Vacuon.Core.Tests;

/// <summary>
/// Milestone M3 — where a photograph was taken.
/// <para>
/// The tests build their own JPEG with coordinates written into it, so the expected answer is
/// known rather than whatever this machine's pictures happen to hold. They are skipped when
/// Python and Pillow are not around to write one; a skip is visible in the run, and passing
/// with nothing to check is not on offer.
/// </para>
/// </summary>
public class GeotagTests : IDisposable
{
    // Chosen for being nowhere near round: an error in the minutes or seconds term shows up.
    private const double ExpectedLatitude = -22.951733;   // 22° 57' 6.24" S
    private const double ExpectedLongitude = -43.210700;  // 43° 12' 38.52" W

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"vacuon-gps-{Guid.NewGuid():N}");

    private static readonly bool CanWriteExif = Probe();

    private const string NeedsPillow = "python with Pillow and piexif is not available to write a geotagged JPEG.";

    private static bool Probe()
    {
        try
        {
            using Process? process = Process.Start(new ProcessStartInfo("python")
            {
                Arguments = "-c \"import piexif, PIL\"",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });

            if (process is null) return false;

            process.StandardError.ReadToEnd();
            process.StandardOutput.ReadToEnd();
            process.WaitForExit(30_000);

            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <param name="altitude">"none", "above" or "below" — the reference byte EXIF keeps apart.</param>
    private string Write(bool withLocation, string altitude = "none")
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory,
            withLocation ? $"tagged-{altitude}.jpg" : "plain.jpg");
        string script = Path.Combine(_directory, "write.py");

        File.WriteAllText(script, """
            import sys
            from PIL import Image
            import piexif

            path, tagged, altitude = sys.argv[1], sys.argv[2] == "1", sys.argv[3]
            img = Image.new("RGB", (64, 48), (30, 120, 200))

            if not tagged:
                img.save(path)
                raise SystemExit

            def dms(d, m, s):
                return ((d, 1), (m, 1), (int(round(s * 100)), 100))

            exif = {
                "0th": {piexif.ImageIFD.Model: b"Synthetic"},
                "Exif": {},
                "GPS": {
                    piexif.GPSIFD.GPSLatitudeRef: b"S",
                    piexif.GPSIFD.GPSLatitude: dms(22, 57, 6.24),
                    piexif.GPSIFD.GPSLongitudeRef: b"W",
                    piexif.GPSIFD.GPSLongitude: dms(43, 12, 38.52),
                },
                "1st": {}, "thumbnail": None,
            }

            if altitude != "none":
                exif["GPS"][piexif.GPSIFD.GPSAltitudeRef] = 0 if altitude == "above" else 1
                exif["GPS"][piexif.GPSIFD.GPSAltitude] = (41230, 100)

            img.save(path, exif=piexif.dump(exif))
            """);

        using Process process = Process.Start(new ProcessStartInfo("python")
        {
            Arguments = $"\"{script}\" \"{path}\" {(withLocation ? 1 : 0)} {altitude}",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        // Read before waiting: a redirected pipe nobody drains fills up and the child blocks
        // forever, which looks from outside like a feature that does not work.
        string errors = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(60_000);

        Assert.True(File.Exists(path), "the helper wrote no file: " + errors);

        return path;
    }

    [SkippableFact]
    public void AGeotaggedPictureGivesBackTheCoordinateItWasWrittenWith()
    {
        Skip.IfNot(CanWriteExif, NeedsPillow);

        MediaInfo info = MediaProbe.Read(Write(withLocation: true));

        Assert.True(info.HasLocation, "the picture carries a position and none was read");

        // Six decimals is about a tenth of a metre; the tolerance here is far looser than
        // that, because what is being checked is the arithmetic, not the receiver.
        Assert.Equal(ExpectedLatitude, info.Latitude!.Value, precision: 4);
        Assert.Equal(ExpectedLongitude, info.Longitude!.Value, precision: 4);
    }

    [SkippableFact]
    public void TheSouthernAndWesternHemispheresComeBackNegative()
    {
        // EXIF stores the magnitude and the hemisphere apart. A coordinate read without its
        // reference letter puts Rio de Janeiro in the North Atlantic.
        Skip.IfNot(CanWriteExif, NeedsPillow);

        MediaInfo info = MediaProbe.Read(Write(withLocation: true));

        Assert.True(info.Latitude < 0, $"latitude came back {info.Latitude}, not southern");
        Assert.True(info.Longitude < 0, $"longitude came back {info.Longitude}, not western");
    }

    [SkippableFact]
    public void APictureWithoutAPositionReportsNone()
    {
        Skip.IfNot(CanWriteExif, NeedsPillow);

        MediaInfo info = MediaProbe.Read(Write(withLocation: false));

        Assert.False(info.HasLocation);
        Assert.Null(info.LocationText);
    }

    [SkippableFact]
    public void TheCoordinateIsPrintedToSixDecimalsAndStops()
    {
        Skip.IfNot(CanWriteExif, NeedsPillow);

        MediaInfo info = MediaProbe.Read(Write(withLocation: true));

        Assert.NotNull(info.LocationText);

        // Not the raw double: a reading with metres of error must not be dressed as a
        // measurement to the millimetre.
        foreach (string half in info.LocationText!.Split(", "))
            Assert.Equal(6, half.Split('.')[1].Length);
    }

    [SkippableFact]
    public void AltitudeAboveSeaLevelComesBackPositive()
    {
        Skip.IfNot(CanWriteExif, NeedsPillow);

        MediaInfo info = MediaProbe.Read(Write(withLocation: true, altitude: "above"));

        Assert.NotNull(info.Altitude);
        Assert.Equal(412.30, info.Altitude!.Value, precision: 2);
    }

    [SkippableFact]
    public void AltitudeBelowSeaLevelComesBackNegative()
    {
        // Windows hands back the magnitude alone: measured on these two files, which differ
        // only in their reference byte, System.GPS.Altitude was 412.3 for BOTH. Taking it at
        // face value puts a photograph from the Dead Sea above the sea instead of below it.
        Skip.IfNot(CanWriteExif, NeedsPillow);

        MediaInfo info = MediaProbe.Read(Write(withLocation: true, altitude: "below"));

        Assert.NotNull(info.Altitude);
        Assert.Equal(-412.30, info.Altitude!.Value, precision: 2);
    }

    [SkippableFact]
    public void APictureWithNoAltitudeReportsNone()
    {
        Skip.IfNot(CanWriteExif, NeedsPillow);

        // The coordinate is there and the altitude is not; one must not stand in for the other.
        MediaInfo info = MediaProbe.Read(Write(withLocation: true));

        Assert.True(info.HasLocation);
        Assert.Null(info.Altitude);
    }

    [Fact]
    public void AFileWithNoMetadataAtAllIsAnswerable()
    {
        Directory.CreateDirectory(_directory);

        string path = Path.Combine(_directory, "not-a-picture.jpg");
        File.WriteAllText(path, "this is text wearing a picture's extension");

        MediaInfo info = MediaProbe.Read(path);

        Assert.False(info.HasLocation);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
        catch (IOException) { }

        GC.SuppressFinalize(this);
    }
}
