import { useMemo, useRef, useState } from 'react'
import { ArrowDownToLine, Copy, FileWarning, FolderOpen, Search, Trash2 } from 'lucide-react'
import { crashes, logs, type LogLine } from '../mock'
import { Button, Card, cx, Dot, SectionTitle } from '../ui'

const levels = ['ALL', 'ERROR', 'WARN', 'INFO', 'DEBUG'] as const

const levelTone: Record<LogLine['level'], string> = {
  ERROR: 'text-danger',
  WARN: 'text-warning',
  INFO: 'text-tx-2',
  DEBUG: 'text-tx-3',
}

export default function Logs() {
  const [level, setLevel] = useState<(typeof levels)[number]>('ALL')
  const [query, setQuery] = useState('')
  const [autoScroll, setAutoScroll] = useState(true)
  const [selected, setSelected] = useState<number | null>(null)
  const bodyRef = useRef<HTMLDivElement>(null)

  const rows = useMemo(
    () =>
      logs.filter((line) => {
        const byLevel = level === 'ALL' || line.level === level
        const byQuery = !query || (line.text + line.source).toLowerCase().includes(query.toLowerCase())
        return byLevel && byQuery
      }),
    [level, query],
  )

  const errors = rows.filter((r) => r.level === 'ERROR').length
  const warns = rows.filter((r) => r.level === 'WARN').length

  return (
    <div className="flex h-[calc(100vh-40px-48px)] min-h-[520px] flex-col gap-4">
      {/* 顶部工具条 */}
      <div className="flex items-center justify-between">
        <div>
          <h1 className="text-[17px] font-semibold tracking-tight text-tx-1">日志与崩溃报告</h1>
          <p className="mt-1 text-[11.5px] text-tx-2">
            Better MC · BMC2 · 本次会话 <span className="font-mono text-tx-3">22:38:01 → 22:41:07</span>
          </p>
        </div>
        <div className="flex items-center gap-2">
          <Button variant="ghost" size="sm" icon={<FolderOpen className="h-3.5 w-3.5" />}>打开日志文件夹</Button>
          <Button variant="ghost" size="sm" icon={<ArrowDownToLine className="h-3.5 w-3.5" />}>导出日志</Button>
          <Button variant="quiet" size="sm" icon={<Trash2 className="h-3.5 w-3.5" />}>清空当前视图</Button>
        </div>
      </div>

      <div className="grid min-h-0 flex-1 grid-cols-[176px_minmax(0,1fr)_264px] gap-4">
        {/* 左：筛选 */}
        <Card className="flex h-full flex-col p-3">
          <SectionTitle>级别</SectionTitle>
          <div className="space-y-1">
            {levels.map((item) => (
              <button
                key={item}
                type="button"
                onClick={() => setLevel(item)}
                className={cx(
                  'flex w-full items-center justify-between rounded-sm px-2 py-1.5 text-[11.5px] transition-colors duration-150',
                  level === item ? 'bg-white/[0.07] text-tx-1' : 'text-tx-3 hover:bg-white/[0.04] hover:text-tx-2',
                )}
              >
                <span className="font-mono">{item}</span>
                <span className="text-[10px] text-tx-3">
                  {item === 'ALL' ? logs.length : logs.filter((l) => l.level === item).length}
                </span>
              </button>
            ))}
          </div>

          <div className="mt-4">
            <SectionTitle>搜索</SectionTitle>
            <div className="flex items-center gap-1.5 rounded-sm border border-border bg-black/20 px-2 py-1.5">
              <Search className="h-3 w-3 text-tx-3" />
              <input
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                placeholder="过滤关键字…"
                className="w-full bg-transparent font-mono text-[11px] text-tx-1 outline-none placeholder:text-tx-3"
              />
            </div>
          </div>

          <label className="mt-4 flex items-center justify-between text-[11.5px] text-tx-2">
            自动滚动
            <button
              type="button"
              onClick={() => setAutoScroll((v) => !v)}
              className={cx(
                'relative h-4 w-8 rounded-full border transition-colors duration-150',
                autoScroll ? 'border-primary/50 bg-primary/30' : 'border-border bg-white/[0.06]',
              )}
              aria-pressed={autoScroll}
            >
              <span
                className={cx(
                  'absolute top-[2px] h-[10px] w-[10px] rounded-full bg-tx-1 transition-all duration-150',
                  autoScroll ? 'left-[16px]' : 'left-[2px]',
                )}
              />
            </button>
          </label>

          <div className="mt-auto space-y-1.5 border-t border-border pt-3 text-[10.5px] text-tx-3">
            <div className="flex items-center gap-1.5"><Dot tone="danger" /> {errors} 条错误</div>
            <div className="flex items-center gap-1.5"><Dot tone="warning" /> {warns} 条警告</div>
            <div className="flex items-center gap-1.5"><Dot tone="success" /> 会话已归档</div>
          </div>
        </Card>

        {/* 中：日志流 */}
        <Card className="flex h-full min-h-0 flex-col overflow-hidden">
          <div className="flex items-center justify-between border-b border-border px-3 py-2">
            <span className="whitespace-nowrap font-mono text-[10.5px] text-tx-3">latest.log · {rows.length} 行 · JetBrains Mono</span>
            <Button
              variant="quiet"
              size="sm"
              icon={<Copy className="h-3 w-3" />}
              disabled={selected === null}
            >
              <span className="whitespace-nowrap">复制选中</span>
            </Button>
          </div>
          <div ref={bodyRef} className="min-h-0 flex-1 overflow-auto px-3 py-2 font-mono text-[11px] leading-[1.65]">
            {rows.length === 0 ? (
              <div className="grid h-full place-items-center text-[11px] text-tx-3">没有匹配的日志行</div>
            ) : (
              rows.map((line) => (
                <button
                  key={line.id}
                  type="button"
                  onClick={() => setSelected(line.id)}
                  className={cx(
                    'flex min-w-max items-start gap-2 whitespace-pre rounded-sm px-1.5 text-left font-mono transition-colors duration-100',
                    selected === line.id
                      ? 'bg-primary/10'
                      : line.level === 'ERROR'
                        ? 'bg-danger/[0.06] hover:bg-danger/[0.1]'
                        : 'hover:bg-white/[0.04]',
                  )}
                >
                  <span className="w-[86px] shrink-0 text-tx-3">{line.time}</span>
                  <span className={cx('w-[44px] shrink-0', levelTone[line.level])}>{line.level}</span>
                  <span className="w-[78px] shrink-0 text-tx-3">{line.source}</span>
                  <span className={cx('whitespace-pre pr-3', line.level === 'ERROR' ? 'text-[#f0b0b5]' : line.level === 'WARN' ? 'text-[#e8cf9d]' : 'text-tx-1')}>
                    {line.text}
                  </span>
                </button>
              ))
            )}
          </div>
        </Card>

        {/* 右：崩溃报告归档 */}
        <Card className="flex h-full min-h-0 flex-col overflow-hidden">
          <div className="flex items-center justify-between border-b border-border px-3 py-2">
            <span className="text-[12px] text-tx-1">崩溃报告归档</span>
            <span className="font-mono text-[10px] text-tx-3">{crashes.length} 份</span>
          </div>
          <div className="min-h-0 flex-1 overflow-auto p-2">
            {crashes.map((c) => (
              <div key={c.id} className="mb-2 rounded-sm border border-border/70 p-2.5 transition-colors hover:border-white/15 hover:bg-white/[0.03]">
                <div className="flex items-center gap-1.5">
                  <FileWarning className="h-3 w-3 text-danger" />
                  <span className="text-[11.5px] text-tx-1">{c.instance}</span>
                </div>
                <div className="mt-1 font-mono text-[10px] text-tx-3">{c.time}</div>
                <div className="mt-1 break-all font-mono text-[10px] text-tx-3">{c.file}</div>
                <div className="mt-1 text-[10.5px] text-tx-2">{c.reason}</div>
                <div className="mt-2 flex items-center gap-1">
                  <Button variant="quiet" size="sm" icon={<Copy className="h-3 w-3" />}>复制报告</Button>
                  <Button variant="quiet" size="sm" icon={<FolderOpen className="h-3 w-3" />}>打开文件夹</Button>
                </div>
              </div>
            ))}
          </div>
          <div className="border-t border-border px-3 py-2 text-[10.5px] leading-relaxed text-tx-3">
            崩溃报告由游戏写入 crash-reports/，此处仅归档与复制，不做分析。
          </div>
        </Card>
      </div>
    </div>
  )
}
