namespace Fable.SignalR.DotNet.Tests

open Expecto

module RunTests =
    [<EntryPoint>]
    let main argv =
        runTestsWithCLIArgs [] argv Client.tests
