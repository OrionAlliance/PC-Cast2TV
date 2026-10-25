sub init()
    m.video = m.top.FindNode("video")
    m.note = m.top.FindNode("note")
    m.leaveTimer = m.top.FindNode("leaveTimer")
    m.video.ObserveField("state", "onState")
    m.leaveTimer.ObserveField("fire", "onLeave")
    m.badge = m.top.FindNode("badge")
    m.badgeText = m.top.FindNode("badgeText")
    m.badgeTimer = m.top.FindNode("badgeTimer")
    m.badgeTimer.ObserveField("fire", "hideBadge")
    m.keys = CreateObject("roSGNode", "KeyTask")
    m.keys.control = "RUN"
    ' The player's own numbers, sent to the PC's log every 10 seconds.
    m.clock = CreateObject("roTimespan")
    m.video.ObserveField("downloadedSegment", "onDownloaded")
    m.video.ObserveField("streamingSegment", "onStreaming")
    m.video.ObserveField("position", "onPosition")
    m.reportTimer = m.top.FindNode("reportTimer")
    m.reportTimer.ObserveField("fire", "sendReport")
    m.played = false
    m.newest = -1
    clearWindow()
    m.casting = false
    m.top.SetFocus(true)
end sub

' The PC's address comes from the cast's own link (http://<PC>:<port>/live/index.m3u8).
sub onCast()
    c = m.top.cast
    if c = invalid or c.url = invalid or c.url = "" then return ' opened from the home screen
    pc = CreateObject("roRegex", "^(http://[^/]+)/", "").Match(c.url)
    if pc.Count() < 2 then return
    print "pc2tv: cast from "; pc[1]
    m.keys.pc = pc[1]
    startReport()
    content = CreateObject("roSGNode", "ContentNode")
    content.url = c.url
    content.streamFormat = "hls"
    content.live = true
    m.video.content = content
    m.video.visible = true
    hideBadge()
    m.video.control = "play"
    m.casting = true
    m.top.SetFocus(true) ' the remote's buttons come here, not to the player
end sub

sub onState()
    state = m.video.state
    print "pc2tv: player "; state
    if state = "playing"
        m.played = true
        if m.stallFrom <> invalid
            m.stallMs = m.stallMs + m.clock.TotalMilliseconds() - m.stallFrom
            m.stallFrom = invalid
        end if
    else if state = "buffering" and m.played and m.stallFrom = invalid
        m.stalls = m.stalls + 1
        m.stallFrom = m.clock.TotalMilliseconds()
    end if
    if state <> "error" and state <> "finished" then return
    if state = "error" then print "pc2tv: player error "; m.video.errorCode; " "; m.video.errorMsg
    m.reportTimer.control = "stop"
    m.video.visible = false
    m.casting = false
    hideBadge()
    m.note.text = "The cast stopped. Start it again from PC Cast2TV on your PC."
end sub

' A new cast starts every count from zero.
sub startReport()
    m.played = false
    m.playing = invalid
    m.newest = -1
    m.lastPos = invalid
    m.castPos = 0#
    m.castWall = 0#
    clearWindow()
    m.reportTimer.control = "stop"
    m.reportTimer.control = "start"
end sub

' The counts for one 10-second report.
sub clearWindow()
    m.pieces = 0
    m.failed = 0
    m.late = 0
    m.slowest = 0
    m.downloadMs = 0#
    m.downloadBytes = 0#
    m.windowPos = 0#
    m.windowWall = 0#
    m.stalls = 0
    m.stallMs = 0
    if m.stallFrom <> invalid then m.stallFrom = m.clock.TotalMilliseconds() ' a stall still going counts in the next report
end sub

' Each piece the player fetched: how long it took against how long it plays.
sub onDownloaded()
    d = m.video.downloadedSegment
    if d = invalid then return
    if num(d.Status) <> 0
        m.failed = m.failed + 1
        return
    end if
    ms = num(d.DownloadDuration)
    length = num(d.SegDuration)
    m.pieces = m.pieces + 1
    m.downloadMs = m.downloadMs + ms
    m.downloadBytes = m.downloadBytes + num(d.SegSize)
    if ms > m.slowest then m.slowest = ms
    if length > 0
        m.pieceMs = length
        if ms > length then m.late = m.late + 1
    end if
    seq = num(d.SegSequence)
    if seq > m.newest then m.newest = seq
end sub

' The piece on screen now; the PC compares it with the newest piece it finished.
sub onStreaming()
    s = m.video.streamingSegment
    if s <> invalid and s.segSequence <> invalid then m.playing = s.segSequence
end sub

' Speed: how much video played against how much time passed, only while playing.
sub onPosition()
    now = m.clock.TotalMilliseconds()
    at = m.video.position
    playing = m.video.state = "playing"
    if playing and m.lastPos <> invalid and m.lastPlaying
        gain = at - m.lastPos
        wall = (now - m.lastAt) / 1000
        m.windowPos = m.windowPos + gain
        m.windowWall = m.windowWall + wall
        m.castPos = m.castPos + gain
        m.castWall = m.castWall + wall
    end if
    m.lastPos = at
    m.lastAt = now
    m.lastPlaying = playing
end sub

' Numbers only; the PC turns them into one line in its log.
sub sendReport()
    q = "pieces=" + txt(m.pieces) + "&failed=" + txt(m.failed) + "&late=" + txt(m.late) + "&slowest=" + txt(m.slowest)
    q = q + "&downloadms=" + txt(m.downloadMs) + "&bytes=" + txt(m.downloadBytes) + "&stalls=" + txt(m.stalls)
    stallMs = m.stallMs
    if m.stallFrom <> invalid then stallMs = stallMs + m.clock.TotalMilliseconds() - m.stallFrom
    q = q + "&stallms=" + txt(stallMs)
    if m.pieceMs <> invalid then q = q + "&piecems=" + txt(m.pieceMs)
    if m.playing <> invalid then q = q + "&playing=" + txt(m.playing)
    if m.newest >= 0 then q = q + "&newest=" + txt(m.newest)
    if m.windowWall > 0 then q = q + "&speed=" + txt(m.windowPos / m.windowWall)
    if m.castWall > 0 then q = q + "&castspeed=" + txt(m.castPos / m.castWall)
    m.keys.report = q
    clearWindow()
end sub

' Player fields come as numbers or as text depending on the field and Roku OS.
function num(x as Dynamic) as Double
    if x = invalid then return 0
    t = Type(x)
    if t = "roString" or t = "String" then return Val(x)
    return x
end function

function txt(x as Dynamic) as String
    return Str(x).Trim()
end function

' The TV runs a few seconds behind the PC, so a press shows at once that it got through; the picture shows the rest.
sub showBadge(text as String)
    m.badgeText.text = text
    m.badge.visible = true
    m.badgeTimer.control = "stop"
    m.badgeTimer.control = "start"
end sub

sub hideBadge()
    m.badgeTimer.control = "stop"
    m.badge.visible = false
end sub

' Each button goes to the PC by name; the PC turns it into the matching key press.
function onKeyEvent(key as String, press as Boolean) as Boolean
    if not m.casting then return false ' waiting card: Back leaves, the rest does nothing
    names = { play: "playpause", OK: "playpause", fastforward: "forward", rewind: "rewind", replay: "rewind", right: "next", left: "previous", back: "back" }
    name = names[key]
    if name = invalid then return false
    if press
        print "pc2tv: button "; key
        if name = "playpause" then showBadge("PLAY/PAUSE")
        m.keys.key = name
        if key = "back" then m.leaveTimer.control = "start" ' the PC ends the cast and sends Home; leave anyway if it doesn't
    end if
    return true
end function

sub onLeave()
    m.top.leave = true
end sub
