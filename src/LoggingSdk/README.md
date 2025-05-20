When Unity calls **MSBuild** to build a project, it **does not automatically define custom environment variables**. However, it does provide **preprocessor directives** (compilation symbols) and **some environment variables** that can be checked during build time.

---

## **🔹 Predefined Compilation Symbols in Unity**
Unity **does not directly expose environment variables to MSBuild**, but it **defines preprocessor symbols** that can be used in your **C# code**. These include:

| Symbol             | Description |
|--------------------|-------------|
| `UNITY_EDITOR`     | Defined when running inside the Unity Editor |
| `UNITY_STANDALONE` | Defined when building for Standalone platforms (Windows, macOS, Linux) |
| `UNITY_ANDROID`    | Defined when building for Android |
| `UNITY_IOS`        | Defined when building for iOS |
| `UNITY_WEBGL`      | Defined when building for WebGL |
| `UNITY_2020_1_OR_NEWER` | Defined if the Unity version is **2020.1 or newer** |
| `UNITY_2021_1_OR_NEWER` | Defined if the Unity version is **2021.1 or newer** |

You **CANNOT** use these in `csproj` files, but you **CAN** use them in your C# code.

Example:
```csharp
#if UNITY_EDITOR
    Debug.Log("Running in Unity Editor");
#elif UNITY_STANDALONE
    Debug.Log("Running in Standalone Build");
#endif
```

---

## **🔹 MSBuild-Specific Environment Variables in Unity**
When **Unity calls MSBuild**, it does not define a unique environment variable like `UNITY_BUILD`. However, Unity **does pass** certain **MSBuild properties** and **environment variables** that can be checked inside the `.csproj` file:

| Environment Variable      | Description |
|--------------------------|-------------|
| `UNITY_THISISABUILD`     | **Sometimes set** by Unity to indicate a Unity build (not guaranteed) |
| `UNITY_VERSION`          | Unity version (e.g., `2021.3.5f1`) |
| `UNITY_PROJECT_PATH`     | Path to the Unity project folder |
| `UNITY_SCRIPTING_BACKEND` | Can be `Mono`, `IL2CPP`, or `DotNet` |
| `UNITY_PLAYER_BUILD_PLATFORM` | The platform being built (Windows, macOS, Android, etc.) |

You can check for `UNITY_VERSION` inside your **build scripts**.

---

## **🔹 Detecting Unity Build in `.csproj`**
Since Unity does **not** provide a dedicated `UNITY_BUILD` environment variable, we can check for **`UNITY_VERSION`**.

Modify your `.csproj` file:

```xml
<PropertyGroup Condition=" '$(UNITY_VERSION)' != '' ">
    <DefineConstants>UNITY;$(DefineConstants)</DefineConstants>
</PropertyGroup>

<PropertyGroup Condition=" '$(UNITY_VERSION)' == '' ">
    <DefineConstants>DOTNET;$(DefineConstants)</DefineConstants>
</PropertyGroup>
```

If Unity **sets `UNITY_VERSION`**, the `UNITY` macro is defined. Otherwise, `DOTNET` is used.

---

## **🔹 Alternative: Custom Environment Variable for Unity Build**
If `UNITY_VERSION` is **not set**, you can **manually define an environment variable** when running MSBuild from Unity.

### **Example: Custom Environment Variable in Unity Build Script**
Modify your Unity **C# Build Script**:

```csharp
using UnityEditor;
using UnityEngine;
using System.Diagnostics;

public class BuildScript
{
    [MenuItem("Build/Build with MSBuild")]
    public static void BuildProject()
    {
        string projectPath = Application.dataPath + "/../";  // Unity project root
        string msBuildPath = @"C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe";

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = msBuildPath,
            Arguments = $"\"{projectPath}MyProject.sln\" /p:UnityBuild=true",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        Process process = new Process { StartInfo = startInfo };
        process.Start();
        UnityEngine.Debug.Log(process.StandardOutput.ReadToEnd());
        process.WaitForExit();
    }
}
```

Then modify your **`.csproj` file**:

```xml
<PropertyGroup Condition="'$(UnityBuild)' == 'true'">
    <DefineConstants>UNITY;$(DefineConstants)</DefineConstants>
</PropertyGroup>
```

Now, Unity will **pass `UnityBuild=true` to MSBuild**.

---

## **🔹 Summary**
| Method | Works in `.csproj`? | Works in `.cs` code? |
|--------|----------------|----------------|
| `UNITY_VERSION` (Env Variable) | ✅ Yes | ❌ No |
| `UNITY_EDITOR`, `UNITY_STANDALONE` (Preprocessor Symbol) | ❌ No | ✅ Yes |
| `UnityBuild=true` (Manual) | ✅ Yes | ✅ Yes |

For a **unified Unity & .NET build**, check for `UNITY_VERSION` in MSBuild or **pass a custom flag** (`UnityBuild=true`). 🚀

Would you like an **automatic script** to detect Unity builds inside MSBuild? 😊