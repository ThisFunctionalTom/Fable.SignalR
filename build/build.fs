open Fake.Core
open Fake.Core.TargetOperators
open Fake.DotNet
open Fake.JavaScript
open Fake.IO
open Fake.IO.FileSystemOperators
open Fake.IO.Globbing.Operators
open Fake.Tools
open Tools.Linting
open Tools.Web
open System

System.Environment.GetCommandLineArgs()
|> Array.skip 1 // skip fsi.exe; build.fsx
|> Array.toList
|> Fake.Core.Context.FakeExecutionContext.Create false __SOURCE_FILE__
|> Fake.Core.Context.RuntimeContext.Fake
|> Fake.Core.Context.setExecutionContext

// The name of the project
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "Fable.SignalR"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "Fable and server bindings for SignalR."

// Author(s) of the project
let author = "Cody Johnson"

// File system information
let solutionFile = "Fable.SignalR.sln"

// Github repo
let repo = "https://github.com/Shmew/Fable.SignalR"

let projectRoot = __SOURCE_DIRECTORY__ @@ "../"

// Files that have bindings to other languages where name linting needs to be more relaxed.
let relaxedNameLinting = 
    [ projectRoot @@ "../src/Fable.SignalR/*.fs"
      projectRoot @@ "../src/Fable.SignalR.Elmish/*.fs"
      projectRoot @@ "../src/Fable.SignalR.Feliz/*.fs"
      projectRoot @@ "../src/Fable.SignalR.Shared/*.fs"
      projectRoot @@ "../tests/**/*.fs" ]

// Read additional information from the release notes document
let release = ReleaseNotes.load (projectRoot @@ "RELEASE_NOTES.md")

// Helper active pattern for project types
let (|Fsproj|Csproj|Vbproj|Shproj|) (projFileName:string) =
    match projFileName with
    | f when f.EndsWith("fsproj") -> Fsproj
    | f when f.EndsWith("csproj") -> Csproj
    | f when f.EndsWith("vbproj") -> Vbproj
    | f when f.EndsWith("shproj") -> Shproj
    | _                           -> failwith (sprintf "Project file %s not supported. Unknown project type." projFileName)
    
let srcGlob        = projectRoot @@ "src" @@ "**" @@ "*.??proj"
let fsSrcGlob      = projectRoot @@ "src" @@ "**" @@ "*.fs"
let fsTestGlob     = projectRoot @@ "tests" @@ "**" @@ "*.fs"
let bin            = projectRoot @@ "bin"
let docs           = projectRoot @@ "docs"
let temp           = projectRoot @@ "temp"
let objFolder      = projectRoot @@ "obj"
let dist           = projectRoot @@ "dist"
let libGlob        = projectRoot @@ "src" @@ "**" @@ "*.fsproj"
let demoGlob       = projectRoot @@ "demo" @@ "**" @@ "*.fsproj"
let dotnetTestGlob = projectRoot @@ "tests" @@ "*DotNet*" @@ "*.fsproj"

let foldExcludeGlobs (g: IGlobbingPattern) (d: string) = g -- d
let foldIncludeGlobs (g: IGlobbingPattern) (d: string) = g ++ d

let fsSrcAndTest =
    !! fsSrcGlob
    ++ fsTestGlob
    -- (projectRoot  @@ "src/**/obj/**")
    -- (projectRoot  @@ "tests/**/obj/**")
    -- (projectRoot  @@ "src/**/AssemblyInfo.*")
    -- (projectRoot  @@ "src/**/**/AssemblyInfo.*")

let fsRelaxedNameLinting =
    let baseGlob s =
        !! s
        -- (projectRoot  @@ "src/**/AssemblyInfo.*")
        -- (projectRoot  @@ "src/**/obj/**")
        -- (projectRoot  @@ "tests/**/obj/**")
    match relaxedNameLinting with
    | [h] when relaxedNameLinting.Length = 1 -> baseGlob h |> Some
    | h::t -> List.fold foldIncludeGlobs (baseGlob h) t |> Some
    | _ -> None

let configuration() =
    FakeVar.getOrDefault "configuration" "Release"

let getEnvFromAllOrNone (s: string) =
    let envOpt (envVar: string) =
        if String.isNullOrEmpty envVar then None
        else Some(envVar)

    let procVar = Environment.GetEnvironmentVariable(s) |> envOpt
    let userVar = Environment.GetEnvironmentVariable(s, EnvironmentVariableTarget.User) |> envOpt
    let machVar = Environment.GetEnvironmentVariable(s, EnvironmentVariableTarget.Machine) |> envOpt

    match procVar,userVar,machVar with
    | Some(v), _, _
    | _, Some(v), _
    | _, _, Some(v)
        -> Some(v)
    | _ -> None

// Set default
FakeVar.set "configuration" "Release"

let killProcs () =
    Process.killAllCreatedProcesses()
    Process.killAllByName "node"
    Process.killAllByName "MSBuild"

Target.createFinal "KillProcess" <| fun _ ->
    killProcs()

// --------------------------------------------------------------------------------------
// Set configuration mode based on target

Target.create "ConfigDebug" <| fun _ ->
    FakeVar.set "configuration" "Debug"

Target.create "ConfigRelease" <| fun _ ->
    FakeVar.set "configuration" "Release"

// --------------------------------------------------------------------------------------
// Clean tasks

Target.create "Clean" <| fun _ ->
    let clean() =
        !! (projectRoot  @@ "tests/**/bin")
        ++ (projectRoot  @@ "tests/**/obj")
        ++ (projectRoot  @@ "tools/bin")
        ++ (projectRoot  @@ "tools/obj")
        ++ (projectRoot  @@ "src/**/bin")
        ++ (projectRoot  @@ "src/**/obj")
        |> Seq.toList
        |> List.append [bin; temp; objFolder; dist]
        |> Shell.cleanDirs
    TaskRunner.runWithRetries clean 10

Target.create "CleanDocs" <| fun _ ->
    let clean() =
        !! (docs @@ "RELEASE_NOTES.md")
        |> List.ofSeq
        |> List.iter Shell.rm

    TaskRunner.runWithRetries clean 10

Target.create "CopyDocFiles" <| fun _ ->
    [ docs @@ "RELEASE_NOTES.md", projectRoot @@ "RELEASE_NOTES.md" ]
    |> List.iter (fun (target, source) -> Shell.copyFile target source)

Target.create "PrepDocs" ignore

// --------------------------------------------------------------------------------------
// Restore tasks

let restoreSolution () =
    solutionFile
    |> DotNet.restore id

Target.create "Restore" <| fun _ ->
    TaskRunner.runWithRetries restoreSolution 5

Target.create "YarnInstall" <| fun _ ->
    if Environment.isWindows then
        let setParams (defaults:Yarn.YarnParams) =
            { defaults with
                Yarn.YarnParams.YarnFilePath = (projectRoot @@ "packages/tooling/Yarnpkg.Yarn/content/bin/yarn.cmd")
            }
        Yarn.install setParams
    else Yarn.install id

Target.create "RebuildSass" <| fun _ ->
    Target.activateFinal "KillProcess"
    TaskRunner.runWithRetries (fun () -> Npm.exec "rebuild node-sass" id) 5

// --------------------------------------------------------------------------------------
// Build tasks

Target.create "Build" <| fun _ ->
    let setParams (defaults:MSBuildParams) =
        { defaults with
            Verbosity = Some(Quiet)
            Targets = ["Build"]
            Properties =
                [
                    "Optimize", "True"
                    "DebugSymbols", "True"
                    "Configuration", configuration()
                    "Version", release.AssemblyVersion
                    "GenerateDocumentationFile", "true"
                    "DependsOnNETStandard", "true"
                ]
         }

    Target.activateFinal "KillProcess"
    restoreSolution()

    !! libGlob
    ++ demoGlob
    ++ dotnetTestGlob
    |> List.ofSeq
    |> List.iter (MSBuild.build setParams)

// --------------------------------------------------------------------------------------
// Lint source code

Target.create "Lint" <| fun _ ->
    fsSrcAndTest
    -- (projectRoot  @@ "src/**/AssemblyInfo.*")
    |> (fun src -> List.fold foldExcludeGlobs src relaxedNameLinting)
    |> (fun fGlob ->
        match fsRelaxedNameLinting with
        | Some(glob) ->
            [(false, fGlob); (true, glob)]
        | None -> [(false, fGlob)])
    |> Seq.map (fun (b,glob) -> (b,glob |> List.ofSeq))
    |> List.ofSeq
    |> FSharpLinter.lintFiles

// --------------------------------------------------------------------------------------
// Run the unit tests

Target.create "RunTests" <| fun _ ->
    Target.activateFinal "KillProcess"
    
    !! (projectRoot @@ "tests" @@ "**" @@ "bin" @@ configuration() @@ "**" @@ "*Tests.exe")
        |> Seq.iter (fun f ->
            killProcs()

            Command.RawCommand(f, Arguments.Empty)
            |> CreateProcess.fromCommand
            |> CreateProcess.withTimeout (System.TimeSpan.MaxValue)
            |> CreateProcess.ensureExitCodeWithMessage "Tests failed."
            |> Proc.run
            |> ignore)

// --------------------------------------------------------------------------------------
// Update package.json version & name    

Target.create "PackageJson" <| fun _ ->
    let setValues (current: Json.JsonPackage) =
        { current with
            Name = Str.toKebabCase project |> Some
            Version = release.NugetVersion |> Some
            Description = summary |> Some
            Homepage = repo |> Some
            Repository = 
                { Json.RepositoryValue.Type = "git" |> Some
                  Json.RepositoryValue.Url = repo |> Some
                  Json.RepositoryValue.Directory = None }
                |> Some
            Bugs = 
                { Json.BugsValue.Url = 
                    @"https://github.com/Shmew/Fable.SignalR/issues/new/choose" |> Some } |> Some
            License = "MIT" |> Some
            Author = author |> Some
            Private = true |> Some }
    
    Json.setJsonPkg setValues

Target.create "Start" <| fun _ ->
    Yarn.exec "start" id 

Target.create "PublishPages" <| fun _ ->
    Yarn.exec "publish-docs" id

// --------------------------------------------------------------------------------------
// Build and release NuGet targets

Target.create "NuGet" <| fun _ ->
    Paket.pack(fun p ->
        { p with
            OutputPath = bin
            Version = release.NugetVersion
            ReleaseNotes = Fake.Core.String.toLines release.Notes
            ProjectUrl = repo
            MinimumFromLockFile = true
            IncludeReferencedProjects = true })

Target.create "NuGetPublish" <| fun _ ->
    Paket.push(fun p ->
        { p with
            ApiKey = 
                match getEnvFromAllOrNone "NUGET_KEY" with
                | Some key -> key
                | None -> failwith "The NuGet API key must be set in a NUGET_KEY environment variable"
            WorkingDir = bin })

// --------------------------------------------------------------------------------------
// Release Scripts

let gitPush msg =
    Git.Staging.stageAll ""
    Git.Commit.exec "" msg
    Git.Branches.push ""

Target.create "GitPush" <| fun p ->
    p.Context.Arguments
    |> List.choose (fun s ->
        match s.StartsWith("--Msg=") with
        | true -> Some(s.Substring 6)
        | false -> None)
    |> List.tryHead
    |> function
    | Some(s) -> s
    | None -> (sprintf "Bump version to %s" release.NugetVersion)
    |> gitPush

Target.create "GitTag" <| fun _ ->
    Git.Branches.tag "" release.NugetVersion
    Git.Branches.pushTag "" "origin" release.NugetVersion

Target.create "PublishDocs" <| fun _ ->
    gitPush "Publishing docs"

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build -t <Target>' to override

Target.create "All" ignore
Target.create "Dev" ignore
Target.create "Release" ignore
Target.create "Publish" ignore
Target.create "CI" ignore

"Clean"
    ==> "Restore"
    ==> "PackageJson"
    ==> "YarnInstall"
    ==> "Lint"
    ==> "Build"
    ==> "RebuildSass"
    ==> "RunTests"

"All"
    ==> "GitPush"
    ?=> "GitTag"

"All" <== ["Lint"; "RunTests"]

"CleanDocs"
    ==> "CopyDocFiles"
    ==> "PrepDocs"

"All"
    ==> "NuGet"
    ?=> "NuGetPublish"

"PrepDocs" 
    ==> "PublishPages"
    ==> "PublishDocs"

"All" 
    ==> "PrepDocs"

"All" 
    ==> "PrepDocs"
    ==> "Start"

"All" ==> "PublishPages"

"ConfigDebug" ?=> "Clean"
"ConfigRelease" ?=> "Clean"

"Dev" <== ["All"; "ConfigDebug"; "Start"]

"Release" <== ["All"; "ConfigRelease"; "NuGet"]

"Publish" <== ["Release"; "NuGetPublish"; "PublishDocs"; "GitTag"; "GitPush" ]

"CI" <== ["RunTests"]

Target.runOrDefaultWithArguments "Dev"
