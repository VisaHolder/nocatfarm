[Wiki home](README.md) · [Every command](COMMANDS.md) · [Front page](../README.md)

# rep4rep

rep4rep is an outside site where people trade Steam profile comments for points. Since 1.9.0 it isn't built into
nocat.farm any more. It's an official [add-on](addons.md) that you install if you want it.

## Setting it up

1. Install it: **Plugins** tab → *Official add-ons* → **Install** next to rep4rep (or type `addons install rep4rep`),
   then restart nocat.farm.
2. On rep4rep.com, open **Settings → API key** and make a key. One key covers all your Steam accounts. No rep4rep account
   yet? [rep4rep.com/?r=reap](https://rep4rep.com/?r=reap).
3. Paste the key under **Plugins → rep4rep → API key**.
4. Switch commenting on for each account that should post: *Post rep4rep comments* in the per-account table under
   Plugins → rep4rep, the **Turn on** buttons on the **rep4rep** tab, or `rep4rep on myaccount` (`rep4rep on all` for every
   account).

*Register accounts automatically* (on) adds each Steam account to rep4rep the first time it's needed, so you don't have
to add anything on their website.

If rep4rep says the key is invalid (it answers 401), nocat.farm tells you: make a new key at rep4rep.com
**Settings → API key** and paste it in again.

**Coming from 1.8.x?** Nothing to redo. When you update, nocat.farm moves your key (still encrypted), each account's
on/off, cap, gaps and hours, any hold that's running, and the comment counts into the add-on's files. Install the add-on
and it picks up from there. Accounts only stay on if *Use rep4rep* was on before.

## Pacing

Steam lets an account leave about 10 comments a day on people who aren't its friends. Go past that and Steam quietly
stops taking comments from that account for about a day. So, per account:

| Setting | Default | |
|---|---|---|
| *Comments a day, at most* | 10 | counted over a rolling 24 hours, and the count survives restarts |
| *Shortest / Longest gap between comments* | 10 / 25 minutes | each gap is random in between; in human mode never under 35 |
| *Only post from / until* | 10:00 / 23:00 | a few random minutes in from each end |
| *Lower the daily cap when Steam says it's reached* | on | Steam's daily-limit refusal below your cap becomes the account's cap (never below 5) |

On top of these, nocat.farm has its own rules that no add-on can get around. Comments are only posted while the account
is up and settled in (in human mode), never on a paused account, and never more than 25 a day per account. See
[Add-ons](addons.md#what-an-add-on-cant-do).

A refused comment means that profile is skipped for a day, and only three different profiles refusing in a row rests
the account for 24 hours. A profile with no comment box for that account (private, or friends only) is skipped without
posting anything.

## Comments it won't post

*Don't post comments with* skips any task whose comment contains one of these words or phrases. Nothing is posted and
another task is picked instead. The default list is `add me, add, free, trade, gift, check my profile`. It matches whole
words in any case, so "add" skips "add me +rep" but not "address". *Don't post comments with links* (on) also skips any
comment with a web address in it. The **Tasks** view on the rep4rep tab shows which ones would be skipped and why.

*Skip comments worth less than* (per account, 0 by default, which is off) does the same by what a task pays. A task under
that many points is never posted, isn't counted against the daily cap, and can't be posted by hand either. With a minimum
set, a task whose points can't be read counts as too low. If every task is too low, the account says it's *waiting for
better-paying tasks* in the `rep4rep` summary and on the tab, and looks again like it does when there are no tasks. The
**Tasks** view and `rep4rep tasks <account>` show what each task pays, and mark the ones skipped. Accounts moved over
from 1.8.x start at 0.

## Commands

```
rep4rep                     # summary
rep4rep points              # points to spend, and points still being checked
rep4rep profiles            # the Steam profiles registered on your rep4rep account
rep4rep tasks myaccount     # comments waiting for one account
rep4rep on myaccount        # switch commenting on (off to stop); 'all' for every account
rep4rep now myaccount       # try a post now (never past the cap, and nocat.farm's rules still apply)
rep4rep pause myaccount     # hold it; rep4rep resume to carry on
rep4rep rest myaccount      # a full day off, then a clean start
rep4rep clear myaccount     # lift a rest and forget refused profiles
rep4rep hold 12             # pause every account for 12 hours; rep4rep hold off ends it
```

`r4r` works as a short name. The **rep4rep** tab shows your points, each account's comments in the last 24 hours, the
hold buttons and each account's queued tasks with a **Post now** button. Points show as pending at first, while
rep4rep checks that the comment went through.

With *Send add-on messages* on (Settings → Notifications), credited comments and rests are sent to Telegram and Discord.

---

[← Phone and notifications](phone-and-notifications.md) · [Settings and commands →](settings.md)
