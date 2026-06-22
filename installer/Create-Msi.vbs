Option Explicit

Dim outputPath, cabPath, manifestPath, version, productCode, upgradeCode
outputPath = WScript.Arguments(0)
cabPath = WScript.Arguments(1)
manifestPath = WScript.Arguments(2)
version = WScript.Arguments(3)
productCode = WScript.Arguments(4)
upgradeCode = WScript.Arguments(5)

Dim installer, database
Set installer = CreateObject("WindowsInstaller.Installer")
Set database = installer.OpenDatabase(outputPath, 3)

Sub Sql(statement)
    Dim view
    On Error Resume Next
    Set view = database.OpenView(statement)
    If Err.Number <> 0 Then
        WScript.Echo "SQL failed: " & statement
        WScript.Echo Err.Description
        WScript.Quit 1
    End If
    view.Execute
    If Err.Number <> 0 Then
        WScript.Echo "SQL execute failed: " & statement
        WScript.Echo Err.Description
        WScript.Quit 1
    End If
    view.Close
    On Error GoTo 0
End Sub

Function Q(value)
    If IsNull(value) Then
        Q = "NULL"
    ElseIf IsNumeric(value) And Not IsEmpty(value) Then
        Q = CStr(value)
    Else
        Q = "'" & Replace(CStr(value), "'", "''") & "'"
    End If
End Function

Sub AddRow(tableName, columns, values)
    Dim i, columnSql, valueSql
    columnSql = ""
    valueSql = ""
    For i = 0 To UBound(columns)
        If i > 0 Then
            columnSql = columnSql & ", "
            valueSql = valueSql & ", "
        End If
        columnSql = columnSql & "`" & columns(i) & "`"
        valueSql = valueSql & Q(values(i))
    Next
    Sql "INSERT INTO `" & tableName & "` (" & columnSql & ") VALUES (" & valueSql & ")"
End Sub

Sub AddStream(name, path)
    Dim view, record
    Set view = database.OpenView("INSERT INTO `_Streams` (`Name`, `Data`) VALUES (?, ?)")
    Set record = installer.CreateRecord(2)
    record.StringData(1) = name
    record.SetStream 2, path
    view.Execute record
    view.Close
End Sub

Sql "CREATE TABLE `Property` (`Property` CHAR(72) NOT NULL, `Value` LONGCHAR LOCALIZABLE PRIMARY KEY `Property`)"
Sql "CREATE TABLE `Directory` (`Directory` CHAR(72) NOT NULL, `Directory_Parent` CHAR(72), `DefaultDir` CHAR(255) NOT NULL LOCALIZABLE PRIMARY KEY `Directory`)"
Sql "CREATE TABLE `Component` (`Component` CHAR(72) NOT NULL, `ComponentId` CHAR(38), `Directory_` CHAR(72) NOT NULL, `Attributes` SHORT NOT NULL, `Condition` CHAR(255), `KeyPath` CHAR(72) PRIMARY KEY `Component`)"
Sql "CREATE TABLE `Feature` (`Feature` CHAR(38) NOT NULL, `Feature_Parent` CHAR(38), `Title` CHAR(64) LOCALIZABLE, `Description` CHAR(255) LOCALIZABLE, `Display` SHORT, `Level` SHORT NOT NULL, `Directory_` CHAR(72), `Attributes` SHORT NOT NULL PRIMARY KEY `Feature`)"
Sql "CREATE TABLE `FeatureComponents` (`Feature_` CHAR(38) NOT NULL, `Component_` CHAR(72) NOT NULL PRIMARY KEY `Feature_`, `Component_`)"
Sql "CREATE TABLE `File` (`File` CHAR(72) NOT NULL, `Component_` CHAR(72) NOT NULL, `FileName` CHAR(255) NOT NULL LOCALIZABLE, `FileSize` LONG NOT NULL, `Version` CHAR(72), `Language` CHAR(20), `Attributes` SHORT, `Sequence` SHORT NOT NULL PRIMARY KEY `File`)"
Sql "CREATE TABLE `Media` (`DiskId` SHORT NOT NULL, `LastSequence` LONG NOT NULL, `DiskPrompt` CHAR(64) LOCALIZABLE, `Cabinet` CHAR(255), `VolumeLabel` CHAR(32), `Source` CHAR(72) PRIMARY KEY `DiskId`)"
Sql "CREATE TABLE `Upgrade` (`UpgradeCode` CHAR(38) NOT NULL, `VersionMin` CHAR(20), `VersionMax` CHAR(20), `Language` CHAR(255), `Attributes` LONG NOT NULL, `Remove` CHAR(255), `ActionProperty` CHAR(72) NOT NULL PRIMARY KEY `UpgradeCode`, `VersionMin`, `VersionMax`, `Language`, `Attributes`)"
Sql "CREATE TABLE `InstallExecuteSequence` (`Action` CHAR(72) NOT NULL, `Condition` CHAR(255), `Sequence` SHORT PRIMARY KEY `Action`)"
Sql "CREATE TABLE `Shortcut` (`Shortcut` CHAR(72) NOT NULL, `Directory_` CHAR(72) NOT NULL, `Name` CHAR(128) NOT NULL LOCALIZABLE, `Component_` CHAR(72) NOT NULL, `Target` CHAR(72) NOT NULL, `Arguments` CHAR(255), `Description` CHAR(255) LOCALIZABLE, `Hotkey` SHORT, `Icon_` CHAR(72), `IconIndex` SHORT, `ShowCmd` SHORT, `WkDir` CHAR(72) PRIMARY KEY `Shortcut`)"

AddRow "Property", Array("Property", "Value"), Array("ProductCode", productCode)
AddRow "Property", Array("Property", "Value"), Array("ProductName", "Windget")
AddRow "Property", Array("Property", "Value"), Array("ProductVersion", version)
AddRow "Property", Array("Property", "Value"), Array("Manufacturer", "Windget")
AddRow "Property", Array("Property", "Value"), Array("UpgradeCode", upgradeCode)
AddRow "Property", Array("Property", "Value"), Array("ALLUSERS", "1")
AddRow "Property", Array("Property", "Value"), Array("ARPNOREPAIR", "1")
AddRow "Property", Array("Property", "Value"), Array("SecureCustomProperties", "OLDWINDGETPRODUCTS")

AddRow "Directory", Array("Directory", "Directory_Parent", "DefaultDir"), Array("TARGETDIR", Null, "SourceDir")
AddRow "Directory", Array("Directory", "Directory_Parent", "DefaultDir"), Array("ProgramFiles64Folder", "TARGETDIR", ".")
AddRow "Directory", Array("Directory", "Directory_Parent", "DefaultDir"), Array("INSTALLFOLDER", "ProgramFiles64Folder", "Windget")
AddRow "Directory", Array("Directory", "Directory_Parent", "DefaultDir"), Array("ProgramMenuFolder", "TARGETDIR", ".")
AddRow "Directory", Array("Directory", "Directory_Parent", "DefaultDir"), Array("ApplicationProgramsFolder", "ProgramMenuFolder", "Windget")
AddRow "Feature", Array("Feature", "Feature_Parent", "Title", "Description", "Display", "Level", "Directory_", "Attributes"), Array("DefaultFeature", Null, "Windget", "Windget desktop widget app", 1, 1, "INSTALLFOLDER", 0)

Dim fso, manifest, line, parts, rowCount, exeComponent, exeFile
Set fso = CreateObject("Scripting.FileSystemObject")
Set manifest = fso.OpenTextFile(manifestPath, 1, False)
rowCount = 0
Do Until manifest.AtEndOfStream
    line = manifest.ReadLine
    If Len(Trim(line)) > 0 Then
        parts = Split(line, vbTab)
        AddRow "Component", Array("Component", "ComponentId", "Directory_", "Attributes", "Condition", "KeyPath"), Array(parts(1), parts(5), "INSTALLFOLDER", 256, Null, parts(0))
        AddRow "FeatureComponents", Array("Feature_", "Component_"), Array("DefaultFeature", parts(1))
        AddRow "File", Array("File", "Component_", "FileName", "FileSize", "Version", "Language", "Attributes", "Sequence"), Array(parts(0), parts(1), parts(2), CLng(parts(3)), Null, Null, 0, CInt(parts(4)))
        If LCase(parts(2)) = "windget.exe" Then
            exeComponent = parts(1)
            exeFile = parts(0)
        End If
        rowCount = rowCount + 1
    End If
Loop
manifest.Close

If Len(exeComponent) > 0 Then
    AddRow "Shortcut", Array("Shortcut", "Directory_", "Name", "Component_", "Target", "Arguments", "Description", "Hotkey", "Icon_", "IconIndex", "ShowCmd", "WkDir"), Array("WindgetShortcut", "ApplicationProgramsFolder", "Windget", exeComponent, "[#" & exeFile & "]", Null, "Windget", Null, Null, Null, Null, "INSTALLFOLDER")
End If

AddRow "Media", Array("DiskId", "LastSequence", "DiskPrompt", "Cabinet", "VolumeLabel", "Source"), Array(1, rowCount, Null, "#Windget.cab", Null, Null)
AddRow "Upgrade", Array("UpgradeCode", "VersionMin", "VersionMax", "Language", "Attributes", "Remove", "ActionProperty"), Array(upgradeCode, Null, version, Null, 1, "ALL", "OLDWINDGETPRODUCTS")

AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("FindRelatedProducts", Null, 25)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("ValidateProductID", Null, 700)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("CostInitialize", Null, 800)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("FileCost", Null, 900)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("CostFinalize", Null, 1000)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("MigrateFeatureStates", "OLDWINDGETPRODUCTS", 1200)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("InstallValidate", Null, 1400)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("RemoveExistingProducts", "OLDWINDGETPRODUCTS", 1450)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("InstallInitialize", Null, 1500)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("ProcessComponents", Null, 1600)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("UnpublishFeatures", Null, 1800)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("RemoveRegistryValues", Null, 2600)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("RemoveShortcuts", Null, 3200)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("RemoveFiles", Null, 3500)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("InstallFiles", Null, 4000)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("CreateShortcuts", Null, 4500)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("RegisterUser", Null, 6000)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("RegisterProduct", Null, 6100)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("PublishFeatures", Null, 6300)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("PublishProduct", Null, 6400)
AddRow "InstallExecuteSequence", Array("Action", "Condition", "Sequence"), Array("InstallFinalize", Null, 6600)

AddStream "Windget.cab", cabPath

Dim summary
Set summary = database.SummaryInformation(20)
summary.Property(2) = "Installation Database"
summary.Property(3) = "Windget Installer"
summary.Property(4) = "Windget"
summary.Property(7) = "x64;1033"
summary.Property(9) = "{000C1084-0000-0000-C000-000000000046}"
summary.Property(14) = 200
summary.Persist

database.Commit
WScript.Echo "Created " & outputPath
