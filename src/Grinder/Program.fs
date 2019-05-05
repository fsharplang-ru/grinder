﻿namespace Grinder

open Funogram
open Grinder
open Grinder.DataAccess
open Grinder.Commands
open Grinder.Types
open Funogram.Api
open Funogram.Bot
open Funogram.Types
open FunogramExt
    
module Program =
    open System.Net.Http
    open System.IO
    open MihaZupan
    open Newtonsoft.Json
    open FSharp.UMX
    open Processing
    
    [<CLIMutable>]
    type Socks5Configuration = {
        Hostname: string
        Port: int
        Username: string
        Password: string
    }

    [<CLIMutable>]
    type BotConfig = {
        Socks5Proxy: Socks5Configuration
        Token: string
        ChatsToMonitor: string array
        AllowedUsers: string array
        ChannelId: int64
        AdminUserId: int64
    }
    
    let createHttpClient config =
        let messageHandler = new HttpClientHandler()
        messageHandler.Proxy <- HttpToSocks5Proxy(config.Hostname, config.Port, config.Username, config.Password)
        messageHandler.UseProxy <- true
        new HttpClient(messageHandler)
   
    let createBotApi config (settings: BotSettings) = {
        new IBotApi with
            member __.DeleteMessage chatId messageId =
                Api.deleteMessage %chatId %messageId
                |> callApiWithDefaultRetry config
                |> Async.Ignore
            
            member __.RestrictUser chatUsername userUsername until =
                ApiExt.restrictUser config %chatUsername %userUsername until
                
            member __.RestrictUserById chatUsername userId until =
                ApiExt.restrictUserById config %chatUsername %userId until
                
            member __.UnrestrictUser chatUsername username =
                ApiExt.unrestrictUser config %chatUsername %username
                
            member __.SendTextToChannel text =
                ApiExt.sendMessage settings.ChannelId config text
            
            member __.PrepareAndDownloadFile fileId =
                ApiExt.prepareAndDownloadFile config fileId
    }
    
    let dataApi = {
        new IDataAccessApi with
            member __.GetUsernameByUserId userId = async {
                match! Datastore.findUsernameByUserId %userId with
                | UsernameFound username ->
                    return Some %(sprintf "@%s" username)
                | UsernameNotFound ->
                    return None
            }
            
            member __.UpsertUsers users =
                Datastore.upsertUsers users
    }    
                
    let onUpdate (settings: BotSettings) (botApi: IBotApi) (dataApi: IDataAccessApi) (context: UpdateContext) =
        async {
            do! UpdateType.fromUpdate settings context.Update
                |> Option.map ^ fun newMessage -> async {
                    match newMessage with
                    | NewAdminPrivateMessage document ->
                        do! processAdminCommand settings context.Config document.FileId
                    | NewUsersAdded users ->
                        do! processNewUsersCommand users
                    | NewMessage message ->
                        match prepareTextMessage context.Me.Username message with
                        | Some message ->
                            let! command = parseAndExecuteTextMessage settings botApi dataApi message
                            do! Logging.logСommandToChannel botApi command
                        | None -> ()
                    | NewReplyMessage reply ->
                        match prepareReplyToMessage context.Me.Username reply with
                        | Some message ->
                            let! command = parseAndExecuteReplyMessage settings botApi dataApi message
                            do! Logging.logСommandToChannel botApi command
                        | None -> ()
                    | IgnoreMessage -> ()
                }
                |> Option.defaultValue Async.Unit
        } |> Async.Start
        
    [<EntryPoint>]
    let main _ =
        let config =
            File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "bot_config.json"))
            |> JsonConvert.DeserializeObject<BotConfig>
        
        let botConfiguration = { 
            defaultConfig with
                Token = config.Token
                Client = createHttpClient config.Socks5Proxy
                AllowedUpdates = ["message"] |> Seq.ofList |> Some
        }

        GrinderContext.MigrateUp()
        
        async {
            printfn "Starting bot"
            
            let settings = {
                Token = config.Token
                ChatsToMonitor = ChatsToMonitor.Create config.ChatsToMonitor
                AllowedUsers = AllowedUsers.Create config.AllowedUsers
                ChannelId = %config.ChannelId
                AdminUserId = %config.AdminUserId
            }
            do! startBot botConfiguration (onUpdate settings (createBotApi botConfiguration settings) dataApi) None
                |> Async.StartChild
                |> Async.Ignore
                
            printfn "Bot started"
            do! Async.Sleep(-1)
        } |> Async.RunSynchronously
        
        printfn "Bot exited"
        0 // return an integer exit code