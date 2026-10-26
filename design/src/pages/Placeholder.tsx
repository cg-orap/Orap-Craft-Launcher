import { Card, PixelArt, Button } from '../ui'
import type { ReactNode } from 'react'

export default function Placeholder({
  title,
  desc,
  icon,
  actions,
}: {
  title: string
  desc: string
  icon?: ReactNode
  actions?: string[]
}) {
  return (
    <Card className="flex flex-col items-center justify-center gap-3 py-16">
      <PixelArt variant="chest" />
      <div className="flex items-center gap-2 text-[14px] text-tx-1">
        {icon}
        {title}
      </div>
      <div className="max-w-[420px] text-center text-[11.5px] leading-relaxed text-tx-3">{desc}</div>
      {actions?.length ? (
        <div className="mt-2 flex flex-wrap justify-center gap-2">
          {actions.map((a) => (
            <Button key={a} variant="ghost" size="sm">{a}</Button>
          ))}
        </div>
      ) : null}
    </Card>
  )
}