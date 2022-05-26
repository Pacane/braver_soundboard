open FSharp.Data
open System.IO
open Newtonsoft.Json
open Discord
open Discord.WebSocket
open Discord.Audio
open Discord.Commands
open System.Threading.Tasks
open System
open System.Reflection
open System.Diagnostics

let token = "OTY5NzU5MjAyMjA3NzU2Mzg4.GRtGnq.iDyI8fd50RxUtkwAMI6h_E3jrRkzPWSv8lEvB8"

let createStream path =
    Process.Start(
        ProcessStartInfo(
            FileName = "ffmpeg",
            //Arguments = $"-hide_banner -loglevel panic -i \"{path}\" -ac 2 -f s16le -ar 44100 pipe:1",
            Arguments =
                $"-hide_banner -loglevel panic -c:a libvorbis -i \"{path}\" -ac 2 -f s16le -ar 44000 -filter:a \"volume=20dB\" pipe:1",
            UseShellExecute = false,
            RedirectStandardOutput = true
        )
    )

let sendAsync (client: IAudioClient, path) =
    task {
        use ffmpeg = createStream path
        use output = ffmpeg.StandardOutput.BaseStream
        use discord = client.CreatePCMStream AudioApplication.Mixed

        ignore
        <| match ffmpeg.ExitCode with
           | 1 -> printfn "Something wrong happened with ffmpeg: %s" (ffmpeg.StandardOutput.ToString())
           | _ -> ()

        do! output.CopyToAsync(discord)
        do! discord.FlushAsync()
    }

type InfoModule() =
    inherit ModuleBase<SocketCommandContext>()

    [<Command("!say"); Summary("Echoes a message")>]
    member public x.sayAsync([<Remainder; Summary("The text to echo")>] echo: string) : Task =
        printfn "sayAsync received %s" (echo.ToString())
        x.ReplyAsync(echo)

type SoundModule() =
    inherit ModuleBase<ICommandContext>()

    [<Command("!sound", RunMode = RunMode.Async); Summary("Plays a sound clip")>]
    member public x.playSound([<Remainder; Summary("The name of the sound clip")>] clipName: string) : Task =
        task {
            printfn "play sound clip %s" (clipName.ToString())
            let guildUser: IVoiceState = downcast (x.Context.User)
            let voiceChannel = guildUser.VoiceChannel

            let! connection = voiceChannel.ConnectAsync()
            do! sendAsync (connection, $"{clipName}.ogg")
            do! connection.StopAsync()
        }

let messageUpdated =
    Func<Cacheable<IMessage, uint64>, SocketMessage, ISocketMessageChannel, Task> (fun before after channel ->
        task {
            let! message = before.GetOrDownloadAsync()
            printfn "%s -> %s" (message.ToString()) (after.ToString())
        })

let log =
    Func<LogMessage, Task>(fun message -> task { printfn "%s" (message.ToString()) })

type CommandHandler(client: DiscordSocketClient, commandsService: CommandService) =
    let handleCommandAsync =
        Func<SocketMessage, Task> (fun messageParam ->
            task {
                printfn "Received some message"
                let message: SocketUserMessage = downcast messageParam

                let argPos = 0

                let startsWithBang = message.HasCharPrefix('!', ref argPos)
                let isMention = message.HasMentionPrefix((client.CurrentUser, ref argPos))
                let isAuthorBot = message.Author.IsBot

                let shouldHandleCommand = not (not startsWithBang || isMention || isAuthorBot)

                match shouldHandleCommand with
                | true ->
                    let context = new SocketCommandContext(client, message)
                    let! result = commandsService.ExecuteAsync(context = context, argPos = argPos, services = null)

                    match result with
                    | result when (not result.IsSuccess) -> printfn "Error %s" (result.ErrorReason)
                    | _ -> ()
                | false -> ()
            })

    member x.installCommandsAsync() =
        task {
            printfn "Installing commands async"
            client.add_MessageReceived handleCommandAsync

            let! a = commandsService.AddModulesAsync(assembly = Assembly.GetEntryAssembly(), services = null)
            ()
        }

let msgHandler =
    Func<SocketMessage, Task> (fun message ->
        task {
            printfn "Received msg %s" (message.ToString())

            if (message.ToString()) = "Hi bot" then
                message.Channel.SendMessageAsync("Hello back to you")
                |> ignore
        })

let identity item = item

[<EntryPoint>]
let main argv =
    let config = DiscordSocketConfig(MessageCacheSize = 100)
    let client = new DiscordSocketClient(config)
    let commandService = new CommandService()
    let commandHandler = CommandHandler(client, commandService)

    task {
        do! client.LoginAsync(tokenType = TokenType.Bot, token = token)
        do! client.StartAsync()

        client.add_Log log

        do! commandHandler.installCommandsAsync ()

        client.add_Ready (fun () ->
            printfn "%s" "Bot is connected"
            Task.CompletedTask)
    }
    |> Async.AwaitTask
    |> Async.RunSynchronously

    Task.Delay(-1)
    |> Async.AwaitTask
    |> Async.RunSynchronously

    0
