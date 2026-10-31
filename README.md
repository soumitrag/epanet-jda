# EPANET-JDA (EPANET Junction Demand Allocator)

EPANET-JDA is a Windows-based utility which calculates junction base demands of an EPANET 2.2 hydraulic network model. It supports two standard demand allocation methodologies: **Unit-Length** and **Equal-Distribution** (refer later sections for detail). The application operates strictly as a pre-processor; it does not execute EPANET hydraulic simulations and does not require the EPANET simulation engine installed on the host machine.

The program reads a user-supplied input (.inp) file of EPANET 2.2, computes junction demands according to user-defined zones and weighting parameters, and writes a separate, verified input (.inp) file. The source file is never overwritten or altered.

### 2. System Requirements and Installation

**Operating System:** Windows 10 or Windows 11.
**Runtime:** Standalone desktop executable (no external installer or runtime setup wizard required).

See the epanetjdahelp.pdf (user guide) file for installation and operation instructions, allocation rules, and troubleshooting.

## Run the standalone Windows application

**Package Archive (epanetjda.zip):** Contains epanetjda.exe, epanetjdahelp.pdf (user guide), and one sample input (.inp) file of EPANET 2.2. 

Download the Windows x64 release ZIP and extract it. Run **epanetjda.exe** from the extracted folder. The packaged executable is self-contained, so no separate .NET runtime installation is required.

## Publish a standalone x64 executable

To publish a self-contained, single-file Windows x64 application:

```powershell
dotnet publish .\Demand_Gen.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The publish output is written to `bin\Release\net10.0-windows\win-x64\publish`. Distribute the executable together with the published help PDF and sample network file.

Review the generated input file before using it in EPANET. 