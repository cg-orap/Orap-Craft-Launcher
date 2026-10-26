import { useState } from 'react'
import { AlertTriangle, Cloud, FolderOpen, HardDriveDownload, RefreshCw, RotateCcw, Trash2 } from 'lucide-react'
import { backups } from '../mock'
import { Button, Card, cx, Dot, Progress, SectionTitle } from '../ui'

const syncStates = {
  synced: { label: '已同步', tone: 'success' as const, desc: '本地与云端一致，最后同步 4 分钟前' },
  syncing: { label: '同步中', tone: 'info' as const, desc: '正在上传 3 个快照（218 MB / 412 MB）' },
  conflict: { label: '存在冲突', tone: 'warning' as const, desc: '同一实例在另一台设备上被修改过，需要你决定保留哪一份' },
}

export default function Backup() {
  const [sync, setSync] = useState<keyof typeof syncStates>('synced')
  const [restore, setRestore] = useState<string | null>(null)
  const info = syncStates[sync]

  return (
    <div className="flex h-[calc(100vh-40px-48px)] min-h-[520px] flex-col gap-4">
      <div className="flex items-end justify-between">
        <div>
          <h1 className="text-[17px] font-semibold tracking-tight text-tx-1">备份与云同步</h1>
          <p className="mt-1 text-[11.5px] text-tx-2">启动前自动备份，超出保留份数后删除最旧的一份。</p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="ghost" size="sm" icon={<FolderOpen className="h-3.5 w-3.5" />}>打开备份目录</Button>
          <Button variant="ghost" size="sm" icon={<RefreshCw className="h-3.5 w-3.5" />} onClick={() => setSync('syncing')}>立即同步</Button>
          <Button variant="primary" size="sm" icon={<HardDriveDownload className="h-3.5 w-3.5" />}>立即备份</Button>
        </div>
      </div>

      {/* 云同步状态条 */}
      <Card className="px-4 py-3">
        <div className="flex items-center justify-between gap-4">
          <div className="flex min-w-0 items-center gap-2.5">
            <Cloud className={cx('h-4 w-4 shrink-0', info.tone === 'warning' ? 'text-warning' : info.tone === 'info' ? 'text-info' : 'text-success')} />
            <div className="min-w-0">
              <div className="flex items-center gap-1.5 text-[12px] text-tx-1">
                <Dot tone={info.tone} pulse={sync === 'syncing'} />
                云同步 · {info.label}
              </div>
              <div className="mt-0.5 truncate text-[10.5px] text-tx-3">{info.desc}</div>
            </div>
          </div>
          <div className="flex shrink-0 items-center gap-1.5">
            {(['synced', 'syncing', 'conflict'] as const).map((s) => (
              <button
                key={s}
                type="button"
                onClick={() => setSync(s)}
                className={cx(
                  'rounded-sm border px-2 py-1 text-[10.5px] transition-colors',
                  sync === s ? 'border-bstrong bg-tint2 text-tx-1' : 'border-border text-tx-3 hover:text-tx-2',
                )}
              >
                {syncStates[s].label}
              </button>
            ))}
          </div>
        </div>
        {sync === 'syncing' ? <div className="mt-3"><Progress value={53} tone="info" /></div> : null}
        {sync === 'conflict' ? (
          <div className="mt-3 flex items-center gap-2 rounded-sm border border-warning/35 bg-warning/[0.07] px-3 py-2">
            <AlertTriangle className="h-3.5 w-3.5 shrink-0 text-warning" />
            <span className="text-[11px] text-tx-2">本地「Better MC · BMC2」22:35 与云端 21:58 不一致</span>
            <span className="ml-auto flex gap-1.5">
              <Button variant="ghost" size="sm">保留本地</Button>
              <Button variant="ghost" size="sm">使用云端</Button>
            </span>
          </div>
        ) : null}
      </Card>

      <div className="grid min-h-0 flex-1 grid-cols-[minmax(0,1fr)_280px] gap-4">
        {/* 备份时间线 */}
        <Card className="min-h-0 overflow-y-auto p-4">
          <SectionTitle hint="共 4 份 · 1.1 GB / 8 GB">备份时间线</SectionTitle>
          <div className="mt-1 divide-y divide-border">
            {backups.map((b, i) => (
              <div key={b.id} className="flex items-center justify-between gap-3 py-3">
                <div className="flex min-w-0 items-center gap-3">
                  <span className={cx('grid h-8 w-8 shrink-0 place-items-center rounded-sm', i === 0 ? 'bg-primary/12 text-primary' : 'bg-tint text-tx-3')}>
                    <HardDriveDownload className="h-3.5 w-3.5" />
                  </span>
                  <div className="min-w-0">
                    <div className="flex items-center gap-2">
                      <span className="truncate text-[12px] text-tx-1">{b.instance}</span>
                      {i === 0 ? <span className="rounded-sm bg-primary/12 px-1.5 py-0.5 text-[10px] text-primary">最新</span> : null}
                    </div>
                    <div className="mt-0.5 font-mono text-[10px] text-tx-3">
                      {b.time} · {b.size} · {b.trigger}
                    </div>
                  </div>
                </div>
                <div className="flex shrink-0 items-center gap-1.5">
                  <Button variant="quiet" size="sm" onClick={() => setRestore(b.time)}>恢复</Button>
                  <Button variant="quiet" size="sm" icon={<Trash2 className="h-3 w-3" />}>删除</Button>
                </div>
              </div>
            ))}
          </div>
        </Card>

        {/* 策略 */}
        <div className="space-y-4">
          <Card className="p-4">
            <SectionTitle>保留策略</SectionTitle>
            {[
              ['触发方式', '启动前 + 每日 03:00'],
              ['保留份数', '7 份'],
              ['包含内容', 'saves · config · mods'],
              ['不包含', '资源缓存 · 日志'],
            ].map(([k, v]) => (
              <div key={k} className="flex items-center justify-between border-b border-border py-2 last:border-0">
                <span className="text-[11px] text-tx-3">{k}</span>
                <span className="font-mono text-[10.5px] text-tx-1">{v}</span>
              </div>
            ))}
          </Card>

          <Card className="p-4">
            <SectionTitle>占用</SectionTitle>
            <div className="space-y-2">
              <div className="flex items-center justify-between text-[10.5px] text-tx-3">
                <span>备份目录</span>
                <span className="font-mono">1.1 GB / 8 GB</span>
              </div>
              <Progress value={14} />
              <div className="font-mono text-[10px] text-tx-3">D:\OrapCraft\backups</div>
            </div>
          </Card>
        </div>
      </div>

      {restore ? (
        <div className="fixed inset-0 z-30 grid place-items-center bg-scrim backdrop-blur-[2px]" onClick={() => setRestore(null)}>
          <Card elevated className="w-[380px] p-5" >
            <div onClick={(e) => e.stopPropagation()}>
              <div className="flex items-center gap-2 text-[13.5px] font-medium text-tx-1">
                <RotateCcw className="h-4 w-4 text-danger" />
                恢复到 {restore} 的备份？
              </div>
              <p className="mt-2 text-[11.5px] leading-relaxed text-tx-2">
                将覆盖当前实例的 saves / config / mods。恢复前会自动把当前状态另存为一份备份，可再次恢复回来。
              </p>
              <div className="mt-4 flex justify-end gap-2">
                <Button variant="quiet" size="sm" onClick={() => setRestore(null)}>取消</Button>
                <Button variant="danger" size="sm" onClick={() => setRestore(null)}>确认恢复</Button>
              </div>
            </div>
          </Card>
        </div>
      ) : null}
    </div>
  )
}