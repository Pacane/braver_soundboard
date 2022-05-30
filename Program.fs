﻿open FSharp.Data
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

let token =
    "OTY5NzU5MjAyMjA3NzU2Mzg4.GRtGnq.iDyI8fd50RxUtkwAMI6h_E3jrRkzPWSv8lEvB8"

let createStream path =
    Process.Start(
        ProcessStartInfo(
            FileName = "ffmpeg",
            Arguments =
                $"-hide_banner -loglevel panic -c:a libvorbis -i \"{path}\" -ac 2 -f s16le -ar 48000 -filter:a \"volume=20dB\" pipe:1",
            UseShellExecute = false,
            RedirectStandardOutput = true
        )
    )

let sendAsync (client: IAudioClient) (path: String) =
    task {
        use ffmpeg = createStream path

        use output =
            ffmpeg.StandardOutput.BaseStream

        try
            use discord =
                client.CreateDirectPCMStream(AudioApplication.Mixed, 48000)
            
            do! output.CopyToAsync(discord)
            do! discord.FlushAsync()
        with
        | e -> Console.WriteLine(e)
    }

type SoundModule() =
    inherit ModuleBase<ICommandContext>()

    [<Command("!sound", RunMode = RunMode.Async); Summary("Plays a sound clip")>]
    member public x.playSound([<Remainder; Summary("The name of the sound clip")>] clipName: string) : Task =
        task {
            printfn $"play sound clip %s{clipName.ToString()}"

            let guildUser: IVoiceState =
                downcast x.Context.User

            let voiceChannel = guildUser.VoiceChannel

            let! connection = voiceChannel.ConnectAsync()
            do! sendAsync connection $"{clipName}.ogg"
            do! connection.StopAsync()
        }

let log =
    Func<LogMessage, Task>(fun message -> task { printfn $"%s{message.ToString()}" })

type CommandHandler(client: DiscordSocketClient, commandsService: CommandService) =
    let handleCommandAsync =
        Func<SocketMessage, Task> (fun messageParam ->
            task {
                printfn "Received some message"

                let message: SocketUserMessage =
                    downcast messageParam

                let argPos = 0

                let startsWithBang =
                    message.HasCharPrefix('!', ref argPos)

                let isMention =
                    message.HasMentionPrefix((client.CurrentUser, ref argPos))

                let isAuthorBot = message.Author.IsBot

                let isAuthorVincent =
                    message.Author.Username = "Vaub"

                let shouldHandleCommand =
                    not (not startsWithBang || isMention || isAuthorBot)
                    && not isAuthorVincent

                match shouldHandleCommand with
                | true ->
                    let context =
                        SocketCommandContext(client, message)

                    let! result = commandsService.ExecuteAsync(context = context, argPos = argPos, services = null)

                    match result with
                    | result when (not result.IsSuccess) -> printfn $"Error %s{result.ErrorReason}"
                    | _ -> ()
                | false -> ()
            })

    member x.installCommandsAsync() =
        task {
            printfn "Installing commands async"
            client.add_MessageReceived handleCommandAsync

            let! _ = commandsService.AddModulesAsync(assembly = Assembly.GetEntryAssembly(), services = null)
            ()
        }

[<EntryPoint>]
let main argv =
    let config =
        DiscordSocketConfig(MessageCacheSize = 100)

    let client = new DiscordSocketClient(config)
    let commandService = new CommandService()

    let commandHandler =
        CommandHandler(client, commandService)

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
