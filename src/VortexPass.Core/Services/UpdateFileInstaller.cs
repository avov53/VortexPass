namespace VortexPass.Core.Services;
public static class UpdateFileInstaller
{
    public static void Install(string staging,string target,Action<string> log,Action<string,string>? copy=null)
    {
        copy??=((from,to)=>File.Copy(from,to,true));
        var backup=Path.Combine(Path.GetTempPath(),"VortexPass-backup",Guid.NewGuid().ToString("N"));
        var installed=new List<(string Destination,bool Existed)>();var keepBackup=false;
        try
        {
            foreach(var source in Directory.GetFiles(staging,"*",SearchOption.AllDirectories))
            {
                var relative=Path.GetRelativePath(staging,source);var destination=Path.Combine(target,relative);var existed=File.Exists(destination);
                if(existed){var saved=Path.Combine(backup,relative);Directory.CreateDirectory(Path.GetDirectoryName(saved)!);copy(destination,saved);}
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                // Register before copying: an I/O failure can leave a partially overwritten file.
                installed.Add((destination,existed));copy(source,destination);log("Installed: "+relative);
            }
        }
        catch
        {
            foreach(var (destination,existed) in installed.AsEnumerable().Reverse())
            {
                try{if(existed)copy(Path.Combine(backup,Path.GetRelativePath(target,destination)),destination);else if(File.Exists(destination))File.Delete(destination);}
                catch(Exception ex){keepBackup=true;log("ROLLBACK ERROR: "+ex.Message+"; recoverable backup: "+backup);}
            }
            throw;
        }
        finally
        {
            if(!keepBackup&&Directory.Exists(backup)){try{Directory.Delete(backup,true);}catch(Exception ex){log("Backup cleanup: "+ex.Message);}}
        }
    }
}
