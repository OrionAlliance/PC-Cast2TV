' PC Cast2TV for Roku: plays the cast the PC opens it with (url or contentId at launch, or an input event while open) and sends buttons back.
sub Main(args as Dynamic)
    screen = CreateObject("roSGScreen")
    port = CreateObject("roMessagePort")
    screen.SetMessagePort(port)
    scene = screen.CreateScene("CastScene")
    screen.Show()
    scene.ObserveField("leave", port)

    input = CreateObject("roInput")
    input.SetMessagePort(port)
    scene.cast = args

    while true
        msg = Wait(0, port)
        kind = Type(msg)
        if kind = "roSGScreenEvent" and msg.IsScreenClosed() then return
        if kind = "roSGNodeEvent" and msg.GetField() = "leave" then return
        if kind = "roInputEvent" and msg.IsInput() then scene.cast = msg.GetInfo()
    end while
end sub
