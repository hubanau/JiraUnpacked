import type {
  HTMLAttributes,
  TdHTMLAttributes,
  ThHTMLAttributes,
} from 'react';

type TableProps = HTMLAttributes<HTMLTableElement>;
type SectionProps = HTMLAttributes<HTMLTableSectionElement>;
type RowProps = HTMLAttributes<HTMLTableRowElement>;
type HeaderCellProps = ThHTMLAttributes<HTMLTableCellElement>;
type DataCellProps = TdHTMLAttributes<HTMLTableCellElement>;

export function Table({ className = '', ...rest }: TableProps) {
  return (
    <div className="overflow-x-auto">
      <table {...rest} className={`w-full text-left text-sm ${className}`} />
    </div>
  );
}

export function THead({ className = '', ...rest }: SectionProps) {
  return (
    <thead
      {...rest}
      className={`border-b border-[var(--ju-border)] text-[var(--ju-muted)] ${className}`}
    />
  );
}

export function TBody({ className = '', ...rest }: SectionProps) {
  return <tbody {...rest} className={className} />;
}

export function TR({ className = '', ...rest }: RowProps) {
  return (
    <tr
      {...rest}
      className={`border-b border-[var(--ju-border)] last:border-0 ${className}`}
    />
  );
}

export function TH({ className = '', ...rest }: HeaderCellProps) {
  return (
    <th
      {...rest}
      scope="col"
      className={`px-3 py-2 font-medium ${className}`}
    />
  );
}

export function TD({ className = '', ...rest }: DataCellProps) {
  return <td {...rest} className={`px-3 py-2 ${className}`} />;
}
