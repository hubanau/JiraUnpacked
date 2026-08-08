import { Link } from 'react-router-dom';
import { EmptyState } from '../ui';

export default function NotFound() {
  return <EmptyState title="Page not found" action={<Link to="/">Back to home</Link>} />;
}
