namespace Core.Model
{
    public interface IEntityObserver<TObservant> where TObservant : Entity
    {
        public void ConnectToEntity(TObservant entity);
    }
}
