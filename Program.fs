open System.Text
open Discord.Interactions
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

let token = Environment.GetEnvironmentVariable "BOT_TOKEN"

let getClipNames =
    File.ReadAllLines("/assets.txt")
    |> Seq.map (fun f -> f.Replace(".ogg", ""))

let bannedUsers = [ "Vaub" ]

let isUserBanned user =
    bannedUsers |> Seq.exists (fun u -> u = user)

let maxCommandNameLength = 32
let maxNumberOfSuggestions = 25

type VolumeFilter() =
    interface IFilter

type UnionContext =
    struct
        val user: IUser
        val guild: IGuild
        new(user: IUser, guild: IGuild) = { user = user; guild = guild }

        new(context: ICommandContext) =
            { user = context.User
              guild = context.Guild }

        new(context: IInteractionContext) =
            { user = context.User
              guild = context.Guild }
    end

type Result<'T> =
    | Success of 'T
    | Failure of Exception

let volume searchType =
    match searchType with
    | SearchType.Direct -> 1.0
    | SearchType.YouTube -> 0.01
    | _ -> failwith "unsupported search type"

let playAudio (context: UnionContext) (node: LavaNode) searchType query =
    task {
        let guildUser: IVoiceState = downcast context.user

        let voiceChannel = guildUser.VoiceChannel

        let! results =
            match searchType with
            | SearchType.Direct -> node.SearchAsync(searchType, $"/assets/%s{query}.ogg")
            | SearchType.YouTube -> node.SearchAsync(searchType, query)
            | _ -> failwith "todo"

        let channel = context.guild

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

        let playSound =
            task {
                let p = player.PlayAsync(Seq.head results.Tracks)

                do! Task.Delay(500)
                do! player.ApplyFilterAsync(VolumeFilter(), volume searchType)
                return p
            }

        let playerPlayerState = player.PlayerState

        do!
            match playerPlayerState with
            | PlayerState.Playing -> Task.CompletedTask
            | PlayerState.None -> playSound
            | PlayerState.Stopped -> playSound
            | PlayerState.Paused -> playSound
            | _ -> Task.CompletedTask
    }

type SoundClipAutoCompleteHandler() =
    inherit AutocompleteHandler()

    override x.GenerateSuggestionsAsync(context, _, _, _) : Task<AutocompletionResult> =
        task {
            let autoCompleteContext: SocketAutocompleteInteraction =
                downcast context.Interaction

            let userInput = autoCompleteContext.Data.Current.Value.ToString()

            return
                getClipNames
                |> Seq.sort
                |> Seq.where (fun c -> c.StartsWith userInput)
                |> Seq.map (fun c ->
                    let truncated = c.Substring(0, min maxCommandNameLength c.Length)

                    AutocompleteResult(truncated, c))
                |> Seq.truncate maxNumberOfSuggestions
                |> Seq.toArray
                |> AutocompletionResult.FromSuccess
        }

type InteractionSoundModule(node: LavaNode) =
    inherit InteractionModuleBase()

    [<SlashCommand("sounds",
                   "List all available sound clips of the soundboard.",
                   true,
                   Discord.Interactions.RunMode.Async)>]
    member public x.sounds() : Task =
        task {
            let clips =
                getClipNames
                |> Seq.fold (fun (acc: StringBuilder) -> acc.AppendLine) (StringBuilder())

            return x.Context.Interaction.RespondAsync(clips.ToString())
        }

    [<SlashCommand("sound", "play sound clip from soundboard", true, Discord.Interactions.RunMode.Async)>]
    member public x.sound
        ([<Summary("ClipName"); Autocomplete(typeof<SoundClipAutoCompleteHandler>)>] clipName: string)
        : Task =
        task {
            let _ =
                try
                    playAudio (UnionContext(x.Context)) node SearchType.Direct clipName
                    |> Async.AwaitTask
                    |> Async.RunSynchronously
                with
                | e -> printfn $"{e.Message}"

            return x.Context.Interaction.RespondAsync $"Played clip {clipName}"
        }

type SoundModule(node: LavaNode) =
    inherit ModuleBase<ICommandContext>()

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
                    let lavaPlayer = node.GetPlayer(x.Context.Guild)

                    let voiceChannel = lavaPlayer.VoiceChannel
                    node.LeaveAsync(voiceChannel)
                | false -> Task.CompletedTask
        }

    [<Command("!volume", RunMode = RunMode.Async); Summary("Sets the volume")>]
    member public x.setVolume([<Remainder; Summary("volume level")>] volume: string) : Task =
        task {
            do!
                match node.HasPlayer(x.Context.Guild) with
                | true ->
                    let newVolume = Convert.ToDouble(volume)

                    node
                        .GetPlayer(x.Context.Guild)
                        .ApplyFilterAsync(VolumeFilter(), volume = newVolume)
                | false -> Task.CompletedTask
        }

    [<Command("!sound", RunMode = RunMode.Async); Summary("Plays a sound clip from the soundboard.")>]
    member public x.playSound([<Remainder; Summary("The name of the sound clip")>] clipName: string) : Task =
        playAudio (UnionContext(x.Context)) node SearchType.Direct clipName

    [<Command("!yt", RunMode = RunMode.Async); Summary("Plays an audio track from youtube")>]
    member public x.playYoutube([<Remainder; Summary("The search query to run on YouTube")>] clipName: string) : Task =
        playAudio (UnionContext(x.Context)) node SearchType.YouTube clipName

let log =
    Func<LogMessage, Task>(fun message -> task { printfn $"%s{message.ToString()}" })

type CommandHandler
    (
        client: DiscordSocketClient,
        commandsService: CommandService,
        interactionsService: InteractionService,
        servicesProvider: IServiceProvider
    ) =
    let lavaNode: LavaNode = servicesProvider.GetRequiredService()

    let handleCommandAsync =
        Func<SocketMessage, Task> (fun messageParam ->
            task {
                let message: SocketUserMessage = downcast messageParam

                let startsWithBang = message.HasCharPrefix('!', ref 0)

                let isMention = message.HasMentionPrefix((client.CurrentUser, ref 0))

                let isAuthorBot = message.Author.IsBot

                let isBannedUser = isUserBanned message.Author.Username

                let shouldHandleCommand =
                    not (not startsWithBang || isMention || isAuthorBot)
                    && not isBannedUser

                match shouldHandleCommand with
                | true ->
                    let context = SocketCommandContext(client, message)

                    let! result =
                        commandsService.ExecuteAsync(context = context, argPos = 0, services = servicesProvider)

                    match result with
                    | result when (not result.IsSuccess) -> printfn $"Error %s{result.ErrorReason}"
                    | _ -> ()
                | false -> ()
            })

    let handleUserSlashCommandAsync =
        Func<SocketSlashCommand, Task> (fun messageParam ->
            task {
                let shouldHandleCommand = not (isUserBanned messageParam.User.Username)

                match shouldHandleCommand with
                | true ->
                    let context = InteractionContext(client, messageParam)

                    let! result =
                        interactionsService.ExecuteCommandAsync(context = context, services = servicesProvider)

                    match result with
                    | result when (not result.IsSuccess) -> printfn $"Error %s{result.ErrorReason}"
                    | _ -> ()
                | false -> ()
            })

    let handleAutoCompleteAsync =
        Func<SocketAutocompleteInteraction, Task> (fun messageParam ->
            task {
                let shouldHandleCommand = true

                match shouldHandleCommand with
                | true ->
                    let context = InteractionContext(client, messageParam)

                    let! result =
                        interactionsService.ExecuteCommandAsync(context = context, services = servicesProvider)

                    match result with
                    | result when (not result.IsSuccess) -> printfn $"Error %s{result.ErrorReason}"
                    | _ -> ()
                | false -> ()
            })

    member x.onReadyAsync =
        Func<Task> (fun messageParam ->
            task {
                try
                    let! _ = interactionsService.RegisterCommandsGloballyAsync(true)
                    ()
                with
                | e ->
                    printfn "Got an error"
                    raise e

                match lavaNode.IsConnected with
                | true -> ()
                | false -> do! lavaNode.ConnectAsync()
            })

    member x.installCommandsAsync(services: IServiceProvider) =
        task {
            client.add_MessageReceived handleCommandAsync

            let! r = commandsService.AddModulesAsync(assembly = Assembly.GetEntryAssembly(), services = services)
            ()
        }

    member x.installInteractionsAsync(services: IServiceProvider) =
        task {
            client.add_AutocompleteExecuted handleAutoCompleteAsync
            client.add_SlashCommandExecuted handleUserSlashCommandAsync

            let! _ = interactionsService.AddModulesAsync(assembly = Assembly.GetEntryAssembly(), services = services)

            ()
        }

[<EntryPoint>]
let main argv =
    let config = DiscordSocketConfig(MessageCacheSize = 100)

    let client = new DiscordSocketClient(config)

    let services =
        ServiceCollection()
            .AddSingleton<DiscordSocketClient>(client)
            .AddLavaNode(fun config ->
                config.Hostname <- Environment.GetEnvironmentVariable "LAVALINK_SVC_SERVICE_HOST"

                config.Port <-
                    Environment.GetEnvironmentVariable "LAVALINK_SVC_SERVICE_PORT"
                    |> UInt16.Parse

                config.Authorization <- Environment.GetEnvironmentVariable "LAVALINK_SERVER_PASSWORD"
                config.SelfDeaf <- false)
            .AddSingleton<SoundClipAutoCompleteHandler>()
            .AddSingleton<SoundModule>()
            .AddSingleton<InteractionSoundModule>()

    let provider = services.BuildServiceProvider()

    let commandService = new CommandService()

    let interactionService = new InteractionService(client)

    let commandHandler =
        CommandHandler(client, commandService, interactionService, provider)

    task {
        do! client.LoginAsync(tokenType = TokenType.Bot, token = token)
        do! client.StartAsync()

        client.add_Log log
        client.add_Ready commandHandler.onReadyAsync

        do! commandHandler.installCommandsAsync provider
        do! commandHandler.installInteractionsAsync provider

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
