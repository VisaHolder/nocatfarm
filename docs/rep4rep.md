[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# rep4rep

rep4rep is an outside site where people trade Steam profile comments for points. Most people won't want it, so it's
off by default: its tab, points and settings stay hidden until you turn on *Use rep4rep at all*
(*Global settings → rep4rep account*).

To set it up, turn that on, open the **rep4rep** tab and paste your API token from rep4rep.com → Settings. It's
checked before it's saved. Then turn it on for each account that should comment: *Post rep4rep comments*,
`set myaccount Rep4Rep true`, or the tab's **Turn it on for all** button. *Register accounts automatically* (on)
adds each account to rep4rep the first time it signs in.

Steam allows about 10 comments per 24 hours on people who aren't your friends. *Most per 24 hours* (10) enforces
that, and the count survives restarts. Comments are spaced 10-25 minutes apart, and only posted between 10:00 and
23:00. If Steam refuses an account while it's still below the cap, it learns that account's real limit (*Learn the
real limit*, on).

```
rep4rep                     # summary
rep4rep points              # points to spend, and points still being checked
rep4rep profiles            # the Steam profiles registered on your rep4rep account
rep4rep tasks myaccount     # comments waiting for one account
rep4rep now myaccount       # post now (never past the daily cap)
rep4rep pause myaccount     # hold it; rep4rep resume to carry on
rep4rep rest myaccount      # a full day off, then a clean start
```

*Hold commenting for (hours)* (Show advanced) pauses every account for that many hours, then carries on by itself.
Points show as pending at first, while rep4rep checks that the comment landed.

---

[← Phone and notifications](phone-and-notifications.md) · [Settings and commands →](settings.md)
