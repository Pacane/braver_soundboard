open System.Text
open Microsoft.Extensions.DependencyInjection
open Discord
open Discord.WebSocket
open Discord.Commands
open System.Threading.Tasks
open System
open System.IO
open System.Reflection
open Victoria
open Victoria.Enums
open Victoria.Filters
open Victoria.Responses.Search

let token =
    "OTY5NzU5MjAyMjA3NzU2Mzg4.GRtGnq.iDyI8fd50RxUtkwAMI6h_E3jrRkzPWSv8lEvB8"

let lavalinkDirectory =
    "/Users/joel/code/java/Lavalink"

type VolumeFilter() =
    interface IFilter

type TextModule() =
    inherit ModuleBase<SocketCommandContext>()

    [<Command("!sounds", RunMode = RunMode.Async); Summary("List available sounds from sound board.")>]
    member public x.listSounds() : Task =
        let stringBuilder =
            Directory.GetFiles($"%s{lavalinkDirectory}/assets")
            |> Seq.map (fun f -> f.Replace($"%s{lavalinkDirectory}/assets/", ""))
            |> Seq.map (fun f -> f.Replace(".ogg", ""))
            |> Seq.fold (fun (acc: StringBuilder) -> acc.AppendLine) (StringBuilder())

        x.Context.Message.ReplyAsync(stringBuilder.ToString())

type SoundModule(node: LavaNode) =
    inherit ModuleBase<ICommandContext>()

    let node = node

    member this.LavaNode = node

    [<Command("!stop", RunMode = RunMode.Async); Summary("Stops playing audio")>]
    member public x.stop() : Task =
        task {
            do!
                match node.HasPlayer(x.Context.Guild) with
                | true -> node.GetPlayer(x.Context.Guild).StopAsync()
                | false -> Task.CompletedTask
        }

    [<Command("!leave", RunMode = RunMode.Async); Summary("Leaves current voice channel")>]
    member public x.leaveVoiceChannel() : Task =
        task {
            do!
                match node.HasPlayer(x.Context.Guild) with
                | true ->
                    let lavaPlayer =
                        node.GetPlayer(x.Context.Guild)

                    let voiceChannel = lavaPlayer.VoiceChannel
                    node.LeaveAsync(voiceChannel)
                | false -> Task.CompletedTask
        }

    [<Command("!sound", RunMode = RunMode.Async); Summary("Plays a sound clip from the soundboard.")>]
    member public x.playSound([<Remainder; Summary("The name of the sound clip")>] clipName: string) : Task =
        task {
            printfn $"play sound clip %s{clipName.ToString()}"

            let guildUser: IVoiceState =
                downcast x.Context.User

            let voiceChannel = guildUser.VoiceChannel

            let! results = node.SearchAsync(SearchType.Direct, $"assets/%s{clipName}.ogg")

            let channel = x.Context.Guild

            let! player =
                match node.HasPlayer(channel) with
                | true ->
                    let lavaPlayer = node.GetPlayer(channel)

                    let newPlayer =
                        if lavaPlayer.VoiceChannel = voiceChannel then
                            Task.FromResult(lavaPlayer)
                        else
                            node.LeaveAsync(lavaPlayer.VoiceChannel)
                            |> Async.AwaitTask
                            |> Async.RunSynchronously

                            node.JoinAsync(voiceChannel)

                    newPlayer
                | false -> node.JoinAsync(voiceChannel)

            let playSound () =
                player.PlayAsync(Seq.head results.Tracks)

            let playerPlayerState = player.PlayerState

            do!
                match playerPlayerState with
                | PlayerState.Playing -> Task.CompletedTask
                | PlayerState.None -> playSound ()
                | PlayerState.Stopped -> playSound ()
                | PlayerState.Paused -> playSound ()
                | _ -> Task.CompletedTask
        }

    [<Command("!yt", RunMode = RunMode.Async); Summary("Plays an audio track from youtube")>]
    member public x.playYoutube([<Remainder; Summary("The search query to run on YouTube")>] clipName: string) : Task =
        task {
            printfn $"play sound clip %s{clipName.ToString()}"

            let guildUser: IVoiceState =
                downcast x.Context.User

            let voiceChannel = guildUser.VoiceChannel

            let! results = node.SearchAsync(SearchType.YouTube, clipName)

            let! player =
                match node.HasPlayer(x.Context.Guild) with
                | true -> Task.FromResult(node.GetPlayer(x.Context.Guild))
                | false -> node.JoinAsync(voiceChannel)

            let playSound () =
                player.PlayAsync(Seq.head results.Tracks)

            let playerPlayerState = player.PlayerState

            let play: Task =
                match playerPlayerState with
                | PlayerState.Playing -> Task.CompletedTask
                | PlayerState.None -> playSound ()
                | PlayerState.Stopped -> playSound ()
                | PlayerState.Paused -> playSound ()
                | _ -> Task.CompletedTask

            let p = Task.Run(fun x -> play)

            do! player.ApplyFilterAsync(VolumeFilter(), volume = 0.01)
            return p
        }

let log =
    Func<LogMessage, Task>(fun message -> task { printfn $"%s{message.ToString()}" })

type CommandHandler(client: DiscordSocketClient, commandsService: CommandService, servicesProvider: IServiceProvider) =
    let lavaNode: LavaNode =
        servicesProvider.GetRequiredService()

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

                    let! result =
                        commandsService.ExecuteAsync(context = context, argPos = argPos, services = servicesProvider)

                    match result with
                    | result when (not result.IsSuccess) -> printfn $"Error %s{result.ErrorReason}"
                    | _ -> ()
                | false -> ()
            })

    let onReadyAsync =
        Func<Task> (fun messageParam ->
            task {
                match lavaNode.IsConnected with
                | true -> ()
                | false -> do! lavaNode.ConnectAsync()
            })

    member x.installCommandsAsync(services: IServiceProvider) =
        task {
            printfn "Installing commands async"
            client.add_MessageReceived handleCommandAsync

            client.add_Ready onReadyAsync

            let! _ = commandsService.AddModulesAsync(assembly = Assembly.GetEntryAssembly(), services = services)
            ()
        }

[<EntryPoint>]
let main argv =
    let config =
        DiscordSocketConfig(MessageCacheSize = 100)

    let client = new DiscordSocketClient(config)

    let services =
        ServiceCollection()
            .AddSingleton<DiscordSocketClient>(client)
            .AddSingleton<LavaNode>()
            .AddSingleton<LavaConfig>(LavaConfig(Authorization = "password"))
            .AddSingleton<SoundModule>()

    let provider =
        services.BuildServiceProvider()

    let commandService = new CommandService()

    let commandHandler =
        CommandHandler(client, commandService, provider)

    task {
        do! client.LoginAsync(tokenType = TokenType.Bot, token = token)
        do! client.StartAsync()

        client.add_Log log

        do! commandHandler.installCommandsAsync provider

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
