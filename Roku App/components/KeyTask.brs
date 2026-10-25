sub init()
    m.top.functionName = "sendLoop"
end sub

sub sendLoop()
    port = CreateObject("roMessagePort")
    m.top.ObserveField("key", port)
    m.top.ObserveField("report", port)
    while true
        msg = Wait(0, port)
        if Type(msg) = "roSGNodeEvent"
            if msg.GetField() = "report"
                send("/report?" + msg.GetData(), "report")
            else
                send("/key/" + msg.GetData(), msg.GetData())
            end if
        end if
    end while
end sub

' GET http://<PC><path>; given up after 2 seconds so a missing PC never holds up the next button.
sub send(path as String, name as String)
    if m.top.pc = "" then return
    xfer = CreateObject("roUrlTransfer")
    reply = CreateObject("roMessagePort")
    xfer.SetMessagePort(reply)
    xfer.SetUrl(m.top.pc + path)
    if not xfer.AsyncGetToString() then return
    msg = Wait(2000, reply)
    if msg = invalid
        xfer.AsyncCancel()
        print "pc2tv: PC did not answer "; name
    else if name <> "report"
        print "pc2tv: PC got "; name; " ("; msg.GetResponseCode(); ")"
    end if
end sub
